using System.Text.Json;

namespace AMK.LasViewer.Engine;

public class EngineException : Exception
{
    public EngineException(string message) : base(message)
    {
    }

    public EngineException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Движок не запущен, не найден или завершился, пока ждали ответа.</summary>
public sealed class EngineUnavailableException : EngineException
{
    public EngineUnavailableException(string message) : base(message)
    {
    }

    public EngineUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Движок ответил на запрос ошибкой JSON-RPC.</summary>
public sealed class EngineRpcException(string method, int code, string message, JsonElement? data)
    : EngineException($"{method}: {message}")
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public string Method { get; } = method;

    public int Code { get; } = code;

    public string RemoteMessage { get; } = message;

    public JsonElement? ErrorData { get; } = data;

    /// <summary>Трассировка Python для внутренних ошибок движка, если она пришла.</summary>
    public string? RemoteTraceback =>
        ErrorData is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("traceback", out JsonElement traceback)
        && traceback.ValueKind == JsonValueKind.String
            ? traceback.GetString()
            : null;
}
