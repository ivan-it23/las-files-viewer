using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AMK.LasViewer.Agent;

/// <summary>
/// Клиент посредника на сайте компании (<c>server/LlmController.php</c>).
/// Ключ polza.ai хранится только у посредника; программа предъявляет
/// встроенный ключ клиента и всегда указывает модель сама.
/// </summary>
public sealed class LlmProxyClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string endpoint;

    public LlmProxyClient(string serverUrl, HttpMessageHandler? handler = null)
    {
        endpoint = BuildEndpoint(serverUrl);
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // Потоковый ответ длится столько, сколько модель думает; ограничение
        // ставится на каждый запрос отдельно.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Key", AgentSettings.BuiltInClientKey);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMK-LasViewer");
    }

    /// <summary>
    /// Адрес файла посредника целиком, например
    /// «https://amk-horizon.ru/las-viewer/LlmController.php». Без схемы — https.
    /// </summary>
    /// <summary>Наибольшая длина ответа модели в токенах (у посредника — MAX_OUTPUT_TOKENS = 8000).</summary>
    public const int MaxOutputTokens = 8000;

    public static string BuildEndpoint(string serverUrl)
    {
        string value = (serverUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Не указан адрес сервера агента.", nameof(serverUrl));
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        // http допускается только для посредника на этом же компьютере — при
        // разработке и проверке; по сети вопросы и данные открытым текстом не уходят.
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("Адрес сервера агента должен начинаться с https://.", nameof(serverUrl));
        }

        return value.TrimEnd('/');
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
        // Длинный скрипт (отчёт Word с графиками) и «размышления» моделей вроде
        // Gemini не умещались в 4000 токенов: ответ обрывался пустым. Посредник
        // ограничивает это число своим MAX_OUTPUT_TOKENS.
        body["max_tokens"] = MaxOutputTokens;

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint + "?a=chat")
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
            throw new LlmProxyException("network_error", $"Нет связи с сервером агента: {exception.Message}");
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
            ? new LlmProxyException("not_deployed", "Сервер агента недоступен: посредник не найден. Сообщите разработчику программы.", 404)
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
