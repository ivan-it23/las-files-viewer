using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AMK.LasViewer.Engine;

/// <summary>Уведомление движка: сообщение JSON-RPC без <c>id</c>.</summary>
public sealed record EngineNotification(string Method, JsonElement? Params);

/// <summary>
/// Клиент JSON-RPC 2.0 поверх текстовых потоков: одно сообщение — одна строка.
/// </summary>
/// <remarks>
/// Ответы сопоставляются с запросами по <c>id</c>, поэтому запросы можно
/// отправлять одновременно из разных потоков. Когда канал закрывается, все
/// ожидающие запросы завершаются <see cref="EngineUnavailableException"/>, а не
/// висят до тайм-аута.
/// </remarks>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextReader reader;
    private readonly TextWriter writer;
    private readonly ConcurrentDictionary<long, PendingRequest> pending = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private long nextId;
    private Task? readLoop;
    private volatile EngineUnavailableException? closedReason;

    public JsonRpcConnection(TextReader reader, TextWriter writer)
    {
        this.reader = reader;
        this.writer = writer;
    }

    public event EventHandler<EngineNotification>? NotificationReceived;

    /// <summary>Строка из канала, которую не удалось понять как сообщение протокола.</summary>
    public event EventHandler<string>? ProtocolViolation;

    public event EventHandler<EngineUnavailableException>? Closed;

    public void Start()
    {
        readLoop ??= Task.Run(ReadLoopAsync);
    }

    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        JsonElement result = await InvokeRawAsync(method, parameters, timeout, cancellationToken);
        if (typeof(TResult) == typeof(JsonElement))
        {
            return (TResult)(object)result;
        }

        try
        {
            return result.Deserialize<TResult>(SerializerOptions)
                ?? throw new EngineException($"{method}: движок вернул пустой результат.");
        }
        catch (JsonException exception)
        {
            throw new EngineException($"{method}: ответ движка не соответствует ожидаемой структуре.", exception);
        }
    }

    public async Task<JsonElement> InvokeRawAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();

        long id = Interlocked.Increment(ref nextId);
        PendingRequest request = new(method);
        pending[id] = request;

        // Канал мог закрыться между проверкой и регистрацией запроса; тогда
        // закрытие уже прошло по списку ожидающих и этот запрос не увидело.
        if (closedReason is { } reason)
        {
            pending.TryRemove(id, out _);
            throw reason;
        }

        try
        {
            string line = JsonSerializer.Serialize(
                new RequestMessage(id, method, parameters),
                SerializerOptions);

            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteAsync(line + "\n");
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                writeLock.Release();
            }

            return await request.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new EngineException(
                $"{method}: движок не ответил за {timeout.TotalSeconds:0} с.");
        }
        catch (IOException exception)
        {
            throw closedReason ?? new EngineUnavailableException("Канал связи с движком закрыт.", exception);
        }
        catch (ObjectDisposedException exception)
        {
            throw closedReason ?? new EngineUnavailableException("Канал связи с движком закрыт.", exception);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close(new EngineUnavailableException("Соединение с движком закрыто приложением."));
        if (readLoop is not null)
        {
            try
            {
                await readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }

        writeLock.Dispose();
    }

    private void ThrowIfClosed()
    {
        if (closedReason is { } reason)
        {
            throw reason;
        }
    }

    private async Task ReadLoopAsync()
    {
        EngineUnavailableException reason;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    HandleLine(line);
                }
            }

            reason = new EngineUnavailableException("Движок закрыл канал связи.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            reason = new EngineUnavailableException("Канал связи с движком оборвался.", exception);
        }

        Close(reason);
    }

    private void HandleLine(string line)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            ProtocolViolation?.Invoke(this, line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            ProtocolViolation?.Invoke(this, line);
            return;
        }

        bool hasId = root.TryGetProperty("id", out JsonElement idElement);
        if (hasId && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long id))
        {
            if (!pending.TryGetValue(id, out PendingRequest? request))
            {
                // Ответ на запрос, который уже снят по тайм-ауту или отмене.
                return;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                request.Completion.TrySetException(ToRpcException(request.Method, error));
            }
            else if (root.TryGetProperty("result", out JsonElement result))
            {
                request.Completion.TrySetResult(result);
            }
            else
            {
                ProtocolViolation?.Invoke(this, line);
            }

            return;
        }

        if (!hasId
            && root.TryGetProperty("method", out JsonElement method)
            && method.ValueKind == JsonValueKind.String)
        {
            JsonElement? parameters = root.TryGetProperty("params", out JsonElement value) ? value : null;
            NotificationReceived?.Invoke(this, new EngineNotification(method.GetString()!, parameters));
            return;
        }

        // Ответ с id = null — движок не смог разобрать запрос. Сопоставить его
        // не с чем, поэтому он уходит в журнал как нарушение протокола.
        ProtocolViolation?.Invoke(this, line);
    }

    private void Close(EngineUnavailableException reason)
    {
        if (Interlocked.CompareExchange(ref closedReason, reason, null) is not null)
        {
            return;
        }

        foreach (PendingRequest request in pending.Values)
        {
            request.Completion.TrySetException(reason);
        }

        Closed?.Invoke(this, reason);
    }

    private static EngineRpcException ToRpcException(string method, JsonElement error)
    {
        int code = error.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int value)
            ? value
            : EngineRpcException.InternalError;
        string message = error.TryGetProperty("message", out JsonElement messageElement)
                         && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()!
            : "ошибка без описания";
        JsonElement? data = error.TryGetProperty("data", out JsonElement dataElement) ? dataElement : null;
        return new EngineRpcException(method, code, message, data);
    }

    private sealed record RequestMessage(long Id, string Method, object? Params)
    {
        public string Jsonrpc => "2.0";
    }

    private sealed class PendingRequest(string method)
    {
        public string Method { get; } = method;

        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
