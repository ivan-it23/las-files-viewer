using System.Text.Json.Nodes;

namespace AMK.LasViewer.Agent;

/// <summary>Вызов инструмента, запрошенный моделью. Аргументы — строка JSON, как её прислала модель.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>Сообщение диалога в формате OpenAI chat/completions.</summary>
public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static ChatMessage System(string text) => new("system", text);

    public static ChatMessage User(string text) => new("user", text);

    public static ChatMessage Assistant(string? text, IReadOnlyList<ToolCall>? toolCalls) =>
        new("assistant", text, toolCalls is { Count: > 0 } ? toolCalls : null);

    public static ChatMessage Tool(string toolCallId, string result) => new("tool", result, null, toolCallId);

    public JsonObject ToJson()
    {
        JsonObject message = new() { ["role"] = Role, ["content"] = Content };
        if (ToolCalls is { Count: > 0 })
        {
            JsonArray calls = [];
            foreach (ToolCall call in ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                });
            }

            message["tool_calls"] = calls;
        }

        if (ToolCallId is not null)
        {
            message["tool_call_id"] = ToolCallId;
        }

        return message;
    }
}

/// <summary>Расход одного запроса к модели.</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, decimal CostRub);

/// <summary>Собранный из потока ответ модели.</summary>
public sealed record ChatCompletion(string Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, ChatUsage? Usage);

/// <summary>Сведения посредника: кто подключён (общий доступ или сотрудник по токену), модель по умолчанию, лимиты.</summary>
public sealed record ProxyStatus(
    bool Shared,
    string UserName,
    string DefaultModel,
    int RequestsPerHour,
    int RequestsLastHour,
    decimal DailyBudgetRub,
    decimal SpentTodayRub,
    bool Configured);

public sealed record ModelInfo(string Id, string Name, int? ContextLength, decimal? PromptPerMillion, decimal? CompletionPerMillion);

/// <summary>Ошибка посредника или сервиса моделей с кодом для выбора сообщения пользователю.</summary>
public sealed class LlmProxyException(string code, string message, int? httpStatus = null) : Exception(message)
{
    public string Code { get; } = code;

    public int? HttpStatus { get; } = httpStatus;

    public bool IsAuthorizationProblem => Code == "unauthorized";
}
