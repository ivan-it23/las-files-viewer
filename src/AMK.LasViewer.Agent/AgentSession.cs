using System.Text.Json.Nodes;

namespace AMK.LasViewer.Agent;

/// <summary>Выполняет инструменты агента — в приложении это движок.</summary>
public interface IAgentToolExecutor
{
    /// <summary>Описания инструментов в формате OpenAI tools.</summary>
    Task<JsonArray> GetToolsAsync(CancellationToken cancellationToken);

    /// <summary>Вызов инструмента; результат — строка JSON для модели.</summary>
    Task<ToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>Результат инструмента: JSON для модели и признак успеха для пользователя.</summary>
public sealed record ToolResult(bool Ok, string Json, string? Error = null);

/// <summary>Что видит пользователь, пока агент работает. Методы вызываются не из потока интерфейса.</summary>
public interface IAgentObserver
{
    void TextDelta(string text);

    void ToolStarted(ToolCall call);

    void ToolFinished(ToolCall call, bool ok, string summary);

    void StepFinished(ChatUsage? usage);
}

/// <summary>
/// Диалог с агентом. Цикл: запрос к модели → вызовы инструментов на рабочей
/// станции → результаты модели → … → ответ. Модель не получает файл целиком,
/// только результаты инструментов.
/// </summary>
public sealed class AgentSession
{
    /// <summary>Сколько раз подряд модель может вызвать инструменты, прежде чем от неё потребуют ответ.</summary>
    public const int MaxToolRounds = 12;

    private const string FinalRoundNote =
        "Лимит обращений к данным на этот вопрос исчерпан. Ответьте по уже полученным результатам инструментов; "
        + "если чего-то не хватило — скажите, что именно стоит уточнить отдельным вопросом.";

    /// <summary>Длина результата инструмента, после которой он обрезается для модели.</summary>
    public const int MaxToolResultChars = 24000;

    private const int MaxHistoryMessages = 120;

    private readonly List<ChatMessage> history = [];

    public IReadOnlyList<ChatMessage> History => history;

    public decimal SpentRub { get; private set; }

    public void Reset()
    {
        history.Clear();
        SpentRub = 0;
    }

    /// <summary>
    /// Отвечает на вопрос пользователя. <paramref name="context"/> — что сейчас
    /// открыто и видно в программе; подставляется в системное сообщение
    /// заново на каждый вопрос.
    /// </summary>
    public async Task<string> AskAsync(
        string question,
        string context,
        LlmProxyClient client,
        string? model,
        IAgentToolExecutor tools,
        IAgentObserver observer,
        CancellationToken cancellationToken)
    {
        TrimHistory();
        int start = history.Count;
        history.Add(ChatMessage.User(question));
        try
        {
            return await RunAsync(context, client, model, tools, observer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Прерванный вопрос убирается целиком: иначе в истории остался бы
            // вызов инструмента без результата, и следующий запрос модель
            // отвергла бы.
            // Если за это время диалог сбросили, откатывать уже нечего.
            if (history.Count > start)
            {
                history.RemoveRange(start, history.Count - start);
            }

            throw;
        }
    }

    private async Task<string> RunAsync(
        string context,
        LlmProxyClient client,
        string? model,
        IAgentToolExecutor tools,
        IAgentObserver observer,
        CancellationToken cancellationToken)
    {
        JsonArray toolList = await tools.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        string system = AgentPrompt.Build(context);

        for (int round = 0; ; round++)
        {
            // После лимита шагов инструменты отключаются: модель обязана
            // ответить по уже собранным данным, а не ходить по кругу.
            bool allowTools = round < MaxToolRounds;
            List<ChatMessage> request = [ChatMessage.System(system), .. history];
            if (!allowTools)
            {
                // Только в запросе, не в истории: в следующих вопросах этой
                // просьбы быть не должно.
                request.Add(ChatMessage.User(FinalRoundNote));
            }

            ChatCompletion completion = await client.CompleteAsync(
                model, request, toolList, allowTools, observer.TextDelta, cancellationToken).ConfigureAwait(false);

            if (completion.Usage is { } usage)
            {
                SpentRub += usage.CostRub;
            }

            observer.StepFinished(completion.Usage);

            if (completion.ToolCalls.Count == 0 || !allowTools)
            {
                string answer = completion.Content.Length > 0
                    ? completion.Content
                    : "Модель не дала ответа. Попробуйте переформулировать вопрос.";
                history.Add(ChatMessage.Assistant(answer, null));
                return answer;
            }

            history.Add(ChatMessage.Assistant(completion.Content.Length > 0 ? completion.Content : null, completion.ToolCalls));
            foreach (ToolCall call in completion.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observer.ToolStarted(call);
                ToolResult result;
                try
                {
                    result = await tools.CallAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Сбой инструмента — сведения для модели, а не конец диалога.
                    result = new ToolResult(false, new JsonObject { ["ok"] = false, ["error"] = exception.Message }.ToJsonString(), exception.Message);
                }

                string json = result.Json.Length > MaxToolResultChars
                    ? result.Json[..MaxToolResultChars] + "…[результат обрезан: уточните интервал или уменьшите число точек]"
                    : result.Json;

                observer.ToolFinished(call, result.Ok, result.Error ?? string.Empty);
                history.Add(ChatMessage.Tool(call.Id, json));
            }
        }
    }

    /// <summary>
    /// Отбрасывает самые старые сообщения, не разрывая пару «вызов инструмента
    /// — результат»: модель отвергает результат без своего вызова.
    /// </summary>
    private void TrimHistory()
    {
        while (history.Count > MaxHistoryMessages)
        {
            history.RemoveAt(0);
            while (history.Count > 0 && history[0].Role != "user")
            {
                history.RemoveAt(0);
            }
        }
    }

}
