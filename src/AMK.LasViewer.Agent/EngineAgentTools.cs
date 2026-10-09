using System.Text.Json;
using System.Text.Json.Nodes;
using AMK.LasViewer.Engine;

namespace AMK.LasViewer.Agent;

/// <summary>Инструменты агента выполняет движок: методы <c>agent.tools</c> и <c>agent.call</c>.</summary>
public sealed class EngineAgentTools(EngineHost engine) : IAgentToolExecutor
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(1);

    private JsonArray? tools;

    public async Task<JsonArray> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (tools is null)
        {
            JsonElement result = await engine.InvokeAsync<JsonElement>("agent.tools", null, cancellationToken).ConfigureAwait(false);
            tools = JsonNode.Parse(result.GetRawText()) as JsonArray
                    ?? throw new EngineException("Движок вернул описание инструментов не списком.");
        }

        return tools;
    }

    public async Task<ToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        JsonElement arguments;
        try
        {
            arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            const string message = "Аргументы вызова — не JSON. Повторите вызов с корректным объектом аргументов.";
            return new ToolResult(false, new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(), message);
        }

        JsonElement result = await engine.InvokeAsync<JsonElement>(
            "agent.call",
            new { Name = name, Arguments = arguments },
            cancellationToken,
            CallTimeout).ConfigureAwait(false);

        bool ok = result.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        string? error = !ok && result.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        return new ToolResult(ok, result.GetRawText(), error);
    }
}
