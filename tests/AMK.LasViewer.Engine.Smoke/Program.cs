using System.Diagnostics;
using AMK.LasViewer.Engine;

// Проверка связки «клиент C# ↔ Python-движок» на настоящем процессе.
// Запуск: dotnet run --project tests/AMK.LasViewer.Engine.Smoke [-- <каталог приложения>]
// Без аргумента движок ищется так же, как в сборке разработчика: вверх от
// каталога вывода до папки engine репозитория. С аргументом — в указанном
// каталоге, например в artifacts\publish\win-x64, чтобы проверить встроенный Python.

string applicationDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : AppContext.BaseDirectory;
int failures = 0;

await using EngineHost host = new(applicationDirectory, "smoke");
host.LogLine += (_, line) => Console.WriteLine($"    | {line}");

await Check("запуск и рукопожатие", async () =>
{
    Assert(await host.StartAsync(CancellationToken.None), host.LastError ?? "движок не запустился");
    Assert(host.State == EngineState.Ready, $"состояние {host.State}");
    EngineInfo info = host.Info!;
    Console.WriteLine(
        $"    Python {info.Python.Version} ({(info.Python.Embedded ? "встроенный" : "системный")}), "
        + $"движок {info.EngineVersion}, PID {info.Pid}, запуск: {host.LaunchSpec!.Describe()}");
});

await Check("кириллица туда и обратно", async () =>
{
    const string payload = "Кийское, RO4_400_S, Ом·м ✓";
    PingResult result = await host.InvokeAsync<PingResult>("system.ping", new { Payload = payload });
    Assert(result.Payload == payload, $"вернулось «{result.Payload}»");
});

await Check("200 одновременных запросов сопоставляются по id", async () =>
{
    PingResult[] results = await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
        host.InvokeAsync<PingResult>("system.ping", new { Payload = index.ToString() })));
    for (int index = 0; index < results.Length; index++)
    {
        Assert(results[index].Payload == index.ToString(), $"запрос {index} получил ответ «{results[index].Payload}»");
    }
});

await Check("неизвестный метод — ошибка -32601", async () =>
{
    EngineRpcException error = await ExpectRpcError(() => host.InvokeAsync<PingResult>("las.nothing"));
    Assert(error.Code == EngineRpcException.MethodNotFound, $"код {error.Code}");
});

await Check("неверный тип параметра — ошибка -32602", async () =>
{
    EngineRpcException error = await ExpectRpcError(() => host.InvokeAsync<PingResult>("system.ping", new { Payload = 5 }));
    Assert(error.Code == EngineRpcException.InvalidParams, $"код {error.Code}");
});

await Check("гибель процесса обнаруживается, ожидающий запрос не висит", async () =>
{
    int pid = host.Info!.Pid;
    TaskCompletionSource<EngineStateChangedEventArgs> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    host.StateChanged += (_, e) =>
    {
        if (e.State == EngineState.Failed)
        {
            failed.TrySetResult(e);
        }
    };

    Process.GetProcessById(pid).Kill();
    EngineStateChangedEventArgs change = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(change.Error!.Contains("неожиданно завершился"), change.Error);

    try
    {
        await host.InvokeAsync<PingResult>("system.ping");
        Assert(false, "вызов упавшего движка должен отказывать");
    }
    catch (EngineUnavailableException)
    {
    }
});

await Check("перезапуск после аварии", async () =>
{
    Assert(await host.StartAsync(CancellationToken.None), host.LastError ?? "перезапуск не удался");
    PingResult result = await host.InvokeAsync<PingResult>("system.ping", new { Payload = "снова" });
    Assert(result.Payload == "снова", result.Payload);
});

await Check("штатная остановка завершает процесс", async () =>
{
    int pid = host.Info!.Pid;
    await host.StopAsync();
    Assert(host.State == EngineState.Stopped, $"состояние {host.State}");
    Assert(!IsRunning(pid), $"процесс {pid} всё ещё работает");
});

Console.WriteLine(failures == 0 ? "Все проверки пройдены." : $"Не пройдено проверок: {failures}.");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> body)
{
    Console.WriteLine($"• {name}");
    try
    {
        await body();
        Console.WriteLine("  OK");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"  ОШИБКА: {exception.GetType().Name}: {exception.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task<EngineRpcException> ExpectRpcError(Func<Task> call)
{
    try
    {
        await call();
    }
    catch (EngineRpcException exception)
    {
        return exception;
    }

    throw new InvalidOperationException("ожидалась ошибка движка");
}

static bool IsRunning(int pid)
{
    try
    {
        using Process process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

internal sealed record PingResult(string Payload);
