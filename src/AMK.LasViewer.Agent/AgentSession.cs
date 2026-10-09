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

    /// <summary>Служебное сообщение для пользователя: например, повтор запроса после обрыва связи.</summary>
    void Notice(string text);
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

    /// <summary>Сколько раз подряд шаг восстанавливается после обрыва связи, прежде чем вопрос считается неудачным.</summary>
    public const int MaxRecoveries = 2;

    /// <summary>Сколько раз повторяется запрос, когда провайдер модели ограничил частоту.</summary>
    public const int MaxBusyRetries = 2;

    /// <summary>Пауза перед повтором при перегрузке провайдера; растёт с каждым повтором. Изменяется проверками.</summary>
    public static TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(4);

    private const string ContinueNote =
        "Ответ оборвался из-за сбоя связи. Продолжите ровно с того места, где он прервался, "
        + "не повторяя уже написанное и без вступлений.";

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

            ChatCompletion completion = await CompleteStepAsync(
                client, model, request, toolList, allowTools, observer, cancellationToken).ConfigureAwait(false);

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
    /// Один шаг — запрос к модели — с восстановлением после обрыва связи.
    /// Если поток оборвался посреди текста, показанное пользователю остаётся,
    /// а модель просят продолжить с места обрыва: в чате это одно сообщение.
    /// Если ничего не пришло, шаг повторяется. После <see cref="MaxRecoveries"/>
    /// обрывов подряд вопрос завершается ошибкой.
    /// </summary>
    private async Task<ChatCompletion> CompleteStepAsync(
        LlmProxyClient client,
        string? model,
        List<ChatMessage> request,
        JsonArray toolList,
        bool allowTools,
        IAgentObserver observer,
        CancellationToken cancellationToken)
    {
        string received = string.Empty;
        List<ChatMessage> current = request;
        int busyRetries = 0;
        for (int failures = 0; ; failures++)
        {
            ChatCompletion part;
            try
            {
                part = await client.CompleteAsync(
                    model, current, toolList, allowTools, observer.TextDelta, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmProxyException exception) when (exception.Code == "provider_busy" && busyRetries < MaxBusyRetries)
            {
                // Провайдер модели ограничил частоту запросов — обычно на
                // несколько секунд. Повтор с паузой, затем — ошибка как есть.
                busyRetries++;
                failures--;
                observer.Notice("Провайдер модели перегружен — повторяю запрос через несколько секунд…");
                await Task.Delay(BusyRetryDelay * busyRetries, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (part.Usage is { } usage)
            {
                SpentRub += usage.CostRub;
            }

            observer.StepFinished(part.Usage);
            if (!part.Interrupted)
            {
                return part with { Content = received + part.Content };
            }

            if (failures >= MaxRecoveries)
            {
                throw new LlmProxyException(
                    "stream_broken",
                    "Связь с сайтом обрывается посреди ответа. Повторите вопрос немного позже или выберите другую модель.");
            }

            received += part.Content;
            if (received.Trim().Length == 0)
            {
                observer.Notice("Связь с сайтом прервалась — повторяю запрос…");
                current = request;
                received = string.Empty;
            }
            else
            {
                current = [.. request, ChatMessage.Assistant(received, null), ChatMessage.User(ContinueNote)];
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
