using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AMK.LasViewer.Engine;

/// <summary>
/// Владеет процессом движка: запуск, рукопожатие, вызовы, остановка и
/// обнаружение аварийного завершения.
/// </summary>
/// <remarks>
/// События приходят из фоновых потоков; окно обязано переносить их в свой
/// диспетчер.
/// </remarks>
public sealed class EngineHost : IAsyncDisposable
{
    public const int SupportedProtocolVersion = 1;

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    // Первый запуск Python на машине с антивирусом бывает заметно медленнее
    // последующих: проверяется каждый загружаемый модуль.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string applicationDirectory;
    private readonly string clientVersion;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly StderrTail stderrTail = new(40);

    private Process? process;
    private JsonRpcConnection? connection;
    private int generation;

    public EngineHost(string applicationDirectory, string clientVersion)
    {
        this.applicationDirectory = applicationDirectory;
        this.clientVersion = clientVersion;
    }

    public EngineState State { get; private set; } = EngineState.NotStarted;

    public EngineInfo? Info { get; private set; }

    public EngineLaunchSpec? LaunchSpec { get; private set; }

    /// <summary>Причина последнего отказа — для показа пользователю.</summary>
    public string? LastError { get; private set; }

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>Строки для журнала: вывод движка в stderr и события жизненного цикла.</summary>
    public event EventHandler<string>? LogLine;

    public event EventHandler<EngineNotification>? NotificationReceived;

    /// <summary>
    /// Запускает движок, а если он уже работает — перезапускает. Отказ не
    /// бросается исключением: он переводит движок в <see cref="EngineState.Failed"/>
    /// с причиной в <see cref="LastError"/>.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            return await StartCoreAsync(cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycleLock.WaitAsync();
        try
        {
            if (State is EngineState.NotStarted or EngineState.Stopped)
            {
                return;
            }

            await StopCoreAsync();
            Info = null;
            SetState(EngineState.Stopped, null);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        JsonRpcConnection? current = connection;
        if (State != EngineState.Ready || current is null)
        {
            throw new EngineUnavailableException(LastError ?? "Движок не запущен.");
        }

        return current.InvokeAsync<TResult>(
            method,
            parameters,
            timeout ?? DefaultRequestTimeout,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifecycleLock.Dispose();
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        Info = null;
        LastError = null;
        stderrTail.Clear();
        SetState(EngineState.Starting, null);

        try
        {
            EngineLaunchSpec spec = EngineLocator.Locate(applicationDirectory);
            LaunchSpec = spec;
            Log($"Запуск движка: {spec.PythonExecutable} (каталог {spec.EngineRoot})");
            JsonRpcConnection started = StartProcess(spec);

            EngineInfo info = await started.InvokeAsync<EngineInfo>(
                "system.hello",
                new
                {
                    Client = "LasViewer",
                    ClientVersion = clientVersion,
                    ProtocolVersion = SupportedProtocolVersion
                },
                HandshakeTimeout,
                cancellationToken);

            if (info.ProtocolVersion != SupportedProtocolVersion)
            {
                throw new EngineUnavailableException(
                    $"Движок работает по протоколу версии {info.ProtocolVersion}, а приложение — "
                    + $"по версии {SupportedProtocolVersion}. Приложение и движок из разных сборок; "
                    + "переустановите приложение.");
            }

            Info = info;
            Log($"Движок готов: Python {info.Python.Version}, движок {info.EngineVersion}, PID {info.Pid}");
            SetState(EngineState.Ready, null);
            return true;
        }
        catch (Exception exception) when (exception is EngineException or Win32Exception or IOException
                                              or InvalidOperationException)
        {
            string message = DescribeStartFailure(exception);
            Log($"Движок не запустился: {message}");
            await StopCoreAsync();
            LastError = message;
            SetState(EngineState.Failed, message);
            return false;
        }
    }

    private JsonRpcConnection StartProcess(EngineLaunchSpec spec)
    {
        ProcessStartInfo startInfo = new(spec.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = spec.EngineRoot
        };

        if (spec.Kind == EngineLaunchKind.Bundled)
        {
            // -I: изолированный режим. Встроенный Python не должен подхватить
            // пакеты или переменные PYTHON* пользователя: движок обязан вести
            // себя одинаково на всех машинах. Путь к пакету задаёт python312._pth.
            startInfo.ArgumentList.Add("-I");
            startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        }
        else
        {
            string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;
            startInfo.Environment["PYTHONPATH"] = existing.Length == 0
                ? spec.EngineRoot
                : spec.EngineRoot + Path.PathSeparator + existing;
        }

        foreach (string argument in new[] { "-X", "utf8", "-u", "-m", "lasviewer_engine" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        Process started = Process.Start(startInfo)
            ?? throw new EngineUnavailableException($"Не удалось запустить {spec.PythonExecutable}.");
        process = started;

        try
        {
            EngineProcessJob.Assign(started);
        }
        catch (Win32Exception exception)
        {
            // Без задания движок всё равно работает и выходит при закрытии
            // канала; теряется только защита от зависшего расчёта.
            Log($"Предупреждение: {exception.Message} ({exception.NativeErrorCode})");
        }

        started.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                stderrTail.Add(line);
                LogLine?.Invoke(this, "[engine] " + line);
            }
        };
        started.BeginErrorReadLine();

        int startedGeneration = Interlocked.Increment(ref generation);
        JsonRpcConnection created = new(started.StandardOutput, started.StandardInput);
        created.NotificationReceived += (_, notification) => NotificationReceived?.Invoke(this, notification);
        created.ProtocolViolation += (_, line) => Log($"Нарушение протокола, строка движка: {line}");
        created.Closed += (_, reason) => OnConnectionClosed(startedGeneration, started, reason);
        connection = created;
        created.Start();
        return created;
    }

    private void OnConnectionClosed(int closedGeneration, Process closedProcess, EngineUnavailableException reason)
    {
        // Закрытие при остановке или перезапуске — штатное. Отказом считается
        // только закрытие работающего движка текущего поколения.
        if (closedGeneration != Volatile.Read(ref generation) || State != EngineState.Ready)
        {
            return;
        }

        string exitCode = closedProcess.WaitForExit(1000)
            ? closedProcess.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "неизвестен";
        string message = $"Движок неожиданно завершился (код выхода {exitCode}).";
        string tail = stderrTail.Snapshot();
        if (tail.Length > 0)
        {
            message += Environment.NewLine + Environment.NewLine + "Последний вывод движка:" + Environment.NewLine + tail;
        }

        Log(message);
        LastError = message;
        Info = null;
        SetState(EngineState.Failed, message);
    }

    private async Task StopCoreAsync()
    {
        // Новое поколение объявляется до остановки: закрытие канала старого
        // процесса не должно выглядеть как авария.
        Interlocked.Increment(ref generation);

        JsonRpcConnection? currentConnection = connection;
        Process? currentProcess = process;
        connection = null;
        process = null;

        if (currentConnection is not null && currentProcess is { HasExited: false })
        {
            try
            {
                await currentConnection.InvokeRawAsync("system.shutdown", null, ShutdownTimeout, CancellationToken.None);
            }
            catch (EngineException)
            {
            }
        }

        if (currentProcess is not null)
        {
            try
            {
                if (!currentProcess.WaitForExit(ShutdownTimeout))
                {
                    Log("Движок не завершился штатно — процесс остановлен принудительно.");
                    currentProcess.Kill(entireProcessTree: true);
                    currentProcess.WaitForExit(ShutdownTimeout);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception exception)
            {
                Log($"Не удалось остановить движок: {exception.Message}");
            }
        }

        if (currentConnection is not null)
        {
            await currentConnection.DisposeAsync();
        }

        currentProcess?.Dispose();
    }

    private string DescribeStartFailure(Exception exception)
    {
        string message = exception switch
        {
            Win32Exception win32 => $"Не удалось запустить Python: {win32.Message}",
            _ => exception.Message
        };

        string tail = stderrTail.Snapshot();
        return tail.Length == 0
            ? message
            : message + Environment.NewLine + Environment.NewLine + "Вывод движка:" + Environment.NewLine + tail;
    }

    private void SetState(EngineState state, string? error)
    {
        State = state;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
    }

    private void Log(string message)
    {
        LogLine?.Invoke(this, message);
    }

    /// <summary>Последние строки stderr движка — для объяснения отказа.</summary>
    private sealed class StderrTail(int capacity)
    {
        private readonly Queue<string> lines = new();

        public void Add(string line)
        {
            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > capacity)
                {
                    lines.Dequeue();
                }
            }
        }

        public void Clear()
        {
            lock (lines)
            {
                lines.Clear();
            }
        }

        public string Snapshot()
        {
            lock (lines)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }
    }
}
