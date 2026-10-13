using System.Text.Json;
using System.Text.Json.Nodes;
using AMK.LasViewer.Engine;

namespace AMK.LasViewer.Agent;

/// <summary>Инструменты агента выполняет движок: методы <c>agent.tools</c> и <c>agent.call</c>.</summary>
/// <remarks>
/// Скрипт (<see cref="ScriptToolName"/>) — особый случай: сначала его код
/// видит и подтверждает пользователь (<paramref name="approver"/>), затем
/// движок выполняет его методом <c>agent.run_script</c> в отдельном процессе.
/// Через <c>agent.call</c> движок скрипт не запускает.
/// </remarks>
/// <param name="scriptsDirectory">Папка, в которой движок создаёт папку на каждый запуск скрипта.</param>
public sealed class EngineAgentTools(EngineHost engine, IScriptApprover? approver = null, string? scriptsDirectory = null)
    : IAgentToolExecutor
{
    public const string ScriptToolName = "run_python";

    // Таймаут скрипта: по умолчанию и пределы. Движок ограничивает его теми же
    // числами (scripts.py) на случай, если вызов придёт не отсюда.
    public const int DefaultScriptTimeoutSeconds = 60;
    public const int MinScriptTimeoutSeconds = 5;
    public const int MaxScriptTimeoutSeconds = 300;

    public const string ScriptRejected =
        "Пользователь отклонил запуск скрипта. Не предлагайте этот же скрипт снова: ответьте по данным других "
        + "инструментов или спросите пользователя, что изменить.";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(1);

    private JsonArray? tools;

    public async Task<JsonArray> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (tools is null)
        {
            JsonElement result = await engine.InvokeAsync<JsonElement>("agent.tools", null, cancellationToken).ConfigureAwait(false);
            JsonArray list = JsonNode.Parse(result.GetRawText()) as JsonArray
                             ?? throw new EngineException("Движок вернул описание инструментов не списком.");
            if (approver is null || scriptsDirectory is null)
            {
                // Подтвердить запуск некому — модель не должна и предлагать скрипт.
                foreach (JsonNode? tool in list.Where(tool => tool?["function"]?["name"]?.GetValue<string>() == ScriptToolName).ToList())
                {
                    list.Remove(tool);
                }
            }

            tools = list;
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
            return Failure("Аргументы вызова — не JSON. Повторите вызов с корректным объектом аргументов.");
        }

        if (name == ScriptToolName)
        {
            return await RunScriptAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        JsonElement result = await engine.InvokeAsync<JsonElement>(
            "agent.call",
            new { Name = name, Arguments = arguments },
            cancellationToken,
            CallTimeout).ConfigureAwait(false);
        return FromEngine(result);
    }

    public async Task<string> GetKnowledgeAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result = await engine.InvokeAsync<JsonElement>(
                "agent.knowledge_context", new { Question = question }, cancellationToken, CallTimeout).ConfigureAwait(false);
            return result.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            // Без материалов агент всё равно ответит — по инструментам и поиску в базе знаний.
            return string.Empty;
        }
    }

    private async Task<ToolResult> RunScriptAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (approver is null || scriptsDirectory is null)
        {
            return Failure("Запуск скриптов в этой программе недоступен.");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Failure("Аргументы должны быть объектом JSON.");
        }

        string code = Text(arguments, "code");
        if (code.Trim().Length == 0)
        {
            return Failure("Пустой скрипт: передайте код в поле code.");
        }

        string title = Text(arguments, "title").Trim();
        string? fileId = Text(arguments, "file_id") is { Length: > 0 } id ? id : null;
        List<string>? fileIds = null;
        if (arguments.TryGetProperty("file_ids", out JsonElement idsElement) && idsElement.ValueKind != JsonValueKind.Null)
        {
            if (idsElement.ValueKind != JsonValueKind.Array || idsElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                return Failure("file_ids должен быть списком идентификаторов файлов.");
            }

            fileIds = idsElement.EnumerateArray().Select(item => item.GetString()!).Distinct().ToList();
        }

        int timeout = DefaultScriptTimeoutSeconds;
        if (arguments.TryGetProperty("timeout_seconds", out JsonElement timeoutElement) && timeoutElement.ValueKind == JsonValueKind.Number)
        {
            timeout = (int)Math.Clamp(Math.Round(timeoutElement.GetDouble()), MinScriptTimeoutSeconds, MaxScriptTimeoutSeconds);
        }

        // Какие файлы получит скрипт — по именам, чтобы пользователь понимал,
        // что подтверждает. Неверный идентификатор — ошибка модели, без вопроса.
        JsonElement open = await engine.InvokeAsync<JsonElement>("las.list", null, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> names = open.EnumerateArray().ToDictionary(
            item => item.GetProperty("file_id").GetString()!,
            item => Path.GetFileName(item.GetProperty("path").GetString()!));
        List<string> selected = fileIds ?? [.. names.Keys];
        if (fileId is not null)
        {
            selected.Remove(fileId);
            selected.Insert(0, fileId);
        }

        List<string> unknown = selected.Where(item => !names.ContainsKey(item)).ToList();
        if (unknown.Count > 0)
        {
            return Failure($"Файлы не открыты: {string.Join(", ", unknown)}. Открыты: {(names.Count > 0 ? string.Join(", ", names.Keys) : "нет")}.");
        }

        if (selected.Count == 0)
        {
            return Failure("В программе нет открытых файлов — скрипту нечего анализировать.");
        }

        ScriptRequest request = new(title, code, selected.Select(item => names[item]).ToList(), timeout);
        if (!await approver.ApproveAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return Failure(ScriptRejected);
        }

        string runId = Guid.NewGuid().ToString("N");
        // Стоп в панели агента — процесс скрипта завершается сразу, а не по таймауту.
        await using CancellationTokenRegistration stop = cancellationToken.Register(() => _ = CancelScriptAsync(runId));
        JsonElement result = await engine.InvokeAsync<JsonElement>(
            "agent.run_script",
            new
            {
                RunId = runId,
                Code = code,
                Title = title,
                FileId = fileId,
                FileIds = selected,
                TimeoutSeconds = timeout,
                ScriptsDir = scriptsDirectory
            },
            cancellationToken,
            TimeSpan.FromSeconds(timeout) + CallTimeout).ConfigureAwait(false);
        return FromEngine(result);
    }

    private async Task CancelScriptAsync(string runId)
    {
        try
        {
            await engine.InvokeAsync<JsonElement>(
                "agent.cancel_script", new { RunId = runId }, CancellationToken.None, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException or OperationCanceledException)
        {
            // Движок перезапущен или закрывается — процесс скрипта завершит задание Windows.
        }
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static ToolResult Failure(string message) =>
        new(false, new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(), message);

    private static ToolResult FromEngine(JsonElement result)
    {
        bool ok = result.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        string? error = !ok && result.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        return new ToolResult(ok, result.GetRawText(), error);
    }
}
