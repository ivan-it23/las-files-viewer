using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AMK.LasViewer.Agent;

/// <summary>
/// Клиент посредника на сайте компании (<c>front.php?c=Llm</c>). Ключ polza.ai
/// и модель — сайта; программа предъявляет встроенный ключ клиента и, если
/// задан, персональный токен сотрудника.
/// </summary>
public sealed class LlmProxyClient : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly string endpoint;

    /// <param name="token">Персональный токен; пусто — общий доступ программы.</param>
    public LlmProxyClient(string serverUrl, string? token = null, HttpMessageHandler? handler = null)
    {
        endpoint = BuildEndpoint(serverUrl);
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // Потоковый ответ длится столько, сколько модель думает; ограничение
        // ставится на каждый запрос отдельно.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Key", AgentSettings.BuiltInClientKey);
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            // Часть конфигураций Apache вырезает Authorization — токен дублируется.
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Token", token.Trim());
        }

        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMK-LasViewer");
    }

    /// <summary>
    /// Адрес точки входа. Принимаются «amk-horizon.ru», «https://amk-horizon.ru»
    /// и «https://amk-horizon.ru/front.php». Без схемы — https: токен не должен
    /// уходить открытым текстом.
    /// </summary>
    public static string BuildEndpoint(string serverUrl)
    {
        string value = (serverUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Не указан адрес сайта.", nameof(serverUrl));
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        // http допускается только для посредника на этом же компьютере — при
        // разработке и проверке; по сети токен открытым текстом не уходит.
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("Адрес сайта должен начинаться с https://: по нему передаётся токен.", nameof(serverUrl));
        }

        value = value.TrimEnd('/');

        // Отдельный файл посредника (server/LlmController.php в папке сайта) —
        // адрес указывается целиком и используется как есть.
        if (value.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith("/front.php", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.EndsWith("/front.php", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/front.php".Length];
        }

        return value + "/front.php";
    }

    public async Task<ProxyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        JsonElement root = await PostJsonAsync("status", new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonElement limits = root.GetProperty("limits");
        return new ProxyStatus(
            !root.TryGetProperty("shared", out JsonElement shared) || shared.ValueKind != JsonValueKind.False,
            Text(root.GetProperty("user"), "full_name") is { Length: > 0 } name ? name : Text(root.GetProperty("user"), "username"),
            Text(root, "default_model"),
            limits.GetProperty("requests_per_hour").GetInt32(),
            limits.GetProperty("requests_last_hour").GetInt32(),
            limits.GetProperty("daily_budget_rub").GetDecimal(),
            limits.GetProperty("spent_today_rub").GetDecimal(),
            root.TryGetProperty("configured", out JsonElement configured) && configured.ValueKind == JsonValueKind.True);
    }

    public async Task<(string DefaultModel, IReadOnlyList<ModelInfo> Models)> GetModelsAsync(CancellationToken cancellationToken)
    {
        JsonElement root = await PostJsonAsync("models", new JsonObject(), cancellationToken).ConfigureAwait(false);
        List<ModelInfo> models = [];
        foreach (JsonElement model in root.GetProperty("models").EnumerateArray())
        {
            models.Add(new ModelInfo(
                Text(model, "id"),
                Text(model, "name") is { Length: > 0 } name ? name : Text(model, "id"),
                Number(model, "context_length") is { } context ? (int)context : null,
                Number(model, "prompt_per_million"),
                Number(model, "completion_per_million")));
        }

        return (Text(root, "default_model"), models);
    }

    /// <summary>
    /// Запрос к модели с потоковым ответом. Текст передаётся в <paramref name="onText"/>
    /// по мере генерации; вызовы инструментов собираются из фрагментов и
    /// возвращаются целиком.
    /// </summary>
    public async Task<ChatCompletion> CompleteAsync(
        string? model,
        IReadOnlyList<ChatMessage> messages,
        JsonArray? tools,
        bool allowTools,
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        JsonObject body = new() { ["messages"] = new JsonArray(messages.Select(message => (JsonNode)message.ToJson()).ToArray()) };
        if (!string.IsNullOrWhiteSpace(model))
        {
            body["model"] = model;
        }

        // Когда инструменты больше нельзя вызывать, их список не передаётся
        // вовсе: при tool_choice "none" часть моделей (DeepSeek через polza.ai)
        // всё равно пытается вызвать инструмент, и сервис отвечает ошибкой.
        if (tools is { Count: > 0 } && allowTools)
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

        body["temperature"] = 0.2;
        body["max_tokens"] = 4000;

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint + "?c=Llm&a=chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmProxyException("network_error", $"Нет связи с сайтом: {exception.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw ErrorFrom(response.StatusCode, text);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, onText, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Разбор потока SSE: текст, вызовы инструментов, итоговый расход, ошибки.</summary>
    public static async Task<ChatCompletion> ReadStreamAsync(TextReader reader, Action<string> onText, CancellationToken cancellationToken)
    {
        StringBuilder content = new();
        SortedDictionary<int, ToolCallBuilder> calls = [];
        string? finishReason = null;
        ChatUsage? usage = null;
        string eventName = string.Empty;
        bool done = false;
        bool interrupted = false;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // Соединение закрыто посреди ответа (ResponseEnded): уже
                // полученное сохраняется, решение о продолжении — за сессией.
                interrupted = true;
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                eventName = string.Empty;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                done = true;
                break;
            }

            JsonElement chunk;
            try
            {
                chunk = JsonDocument.Parse(data).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            if (eventName == "proxy_error")
            {
                throw new LlmProxyException(Text(chunk, "code") is { Length: > 0 } code ? code : "upstream_error", Text(chunk, "message"));
            }

            if (chunk.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                string message = Text(error, "message") is { Length: > 0 } text ? text : "Сервис моделей вернул ошибку.";
                // Провайдер модели перегружен — временно; сессия повторит запрос.
                string code = Text(error, "code") is "TOO_MANY_REQUESTS" or "RATE_LIMITED" ? "provider_busy" : "upstream_error";
                throw new LlmProxyException(code, message);
            }

            if (chunk.TryGetProperty("usage", out JsonElement usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = new ChatUsage(
                    (int)(Number(usageElement, "prompt_tokens") ?? 0),
                    (int)(Number(usageElement, "completion_tokens") ?? 0),
                    Number(usageElement, "cost_rub") ?? Number(usageElement, "cost") ?? 0);
            }

            if (!chunk.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                }

                // Часть моделей присылает итог целиком в message, а не по частям в delta.
                JsonElement delta = choice.TryGetProperty("delta", out JsonElement d) ? d
                    : choice.TryGetProperty("message", out JsonElement m) ? m
                    : default;
                if (delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    string piece = text.GetString() ?? string.Empty;
                    if (piece.Length > 0)
                    {
                        content.Append(piece);
                        onText(piece);
                    }
                }

                if (delta.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    int position = 0;
                    foreach (JsonElement call in toolCalls.EnumerateArray())
                    {
                        int index = call.TryGetProperty("index", out JsonElement indexElement) && indexElement.TryGetInt32(out int value)
                            ? value
                            : position;
                        position++;
                        if (!calls.TryGetValue(index, out ToolCallBuilder? builder))
                        {
                            builder = new ToolCallBuilder();
                            calls[index] = builder;
                        }

                        if (Text(call, "id") is { Length: > 0 } id)
                        {
                            builder.Id = id;
                        }

                        if (call.TryGetProperty("function", out JsonElement function))
                        {
                            // Имя приходит в первом фрагменте; часть сервисов
                            // повторяет его в каждом — повтор не дописывается.
                            if (Text(function, "name") is { Length: > 0 } name && name != builder.Name)
                            {
                                builder.Name += name;
                            }

                            if (function.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind == JsonValueKind.String)
                            {
                                builder.Arguments.Append(arguments.GetString());
                            }
                        }
                    }
                }
            }
        }

        // Поток, закрытый без [DONE] и без причины завершения, тоже оборван:
        // посредник о своих ошибках сообщает событием, а не молчанием.
        if (!done && finishReason is null)
        {
            interrupted = true;
        }

        List<ToolCall> result = [];
        foreach ((int index, ToolCallBuilder builder) in calls)
        {
            // Вызовы из оборванного потока могли прийти не целиком — их
            // аргументам нельзя доверять.
            if (builder.Name.Length == 0 || interrupted)
            {
                continue;
            }

            string id = builder.Id.Length > 0 ? builder.Id : "call_" + index.ToString(CultureInfo.InvariantCulture);
            string arguments = builder.Arguments.Length > 0 ? builder.Arguments.ToString() : "{}";
            result.Add(new ToolCall(id, builder.Name, arguments));
        }

        return new ChatCompletion(content.ToString(), result, finishReason, usage, interrupted);
    }

    public void Dispose() => http.Dispose();

    private async Task<JsonElement> PostJsonAsync(string action, JsonObject body, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ShortTimeout);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint + "?c=Llm&a=" + action)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmProxyException("network_error", $"Нет связи с сайтом: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmProxyException("timeout", "Сайт не ответил за 30 секунд.");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ErrorFrom(response.StatusCode, text);
            }

            try
            {
                JsonElement root = JsonDocument.Parse(text).RootElement;
                if (root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True)
                {
                    return root;
                }

                throw ErrorFrom(response.StatusCode, text);
            }
            catch (JsonException)
            {
                throw new LlmProxyException("bad_response", NotJsonMessage(text));
            }
        }
    }

    private static LlmProxyException ErrorFrom(HttpStatusCode status, string text)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(text).RootElement;
            string code = Text(root, "code");
            string message = Text(root, "message");
            if (code.Length > 0 || message.Length > 0)
            {
                return new LlmProxyException(code.Length > 0 ? code : "error", message.Length > 0 ? message : $"Ошибка {(int)status}.", (int)status);
            }
        }
        catch (JsonException)
        {
        }

        return status == HttpStatusCode.NotFound
            ? new LlmProxyException("not_deployed", "На сайте нет посредника для ИИ-агента (front.php?c=Llm). Его нужно выложить на хостинг.", 404)
            : new LlmProxyException("bad_response", $"Сайт ответил кодом {(int)status}. {NotJsonMessage(text)}", (int)status);
    }

    private static string NotJsonMessage(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? "Пустой ответ." : "Ответ не похож на ответ посредника: " + (trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static decimal? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => null
        };
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }
}
