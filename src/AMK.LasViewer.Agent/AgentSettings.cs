using System.Text;
using System.Text.Json;

namespace AMK.LasViewer.Agent;

/// <summary>
/// Подключение к ИИ-агенту. Пользователю ничего настраивать не нужно:
/// посредник подставляет ключ polza.ai, программа предъявляет встроенный
/// ключ клиента, а модель выбирается в панели агента из <see cref="Models"/>.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Посредник на сайте компании: server/LlmController.php, выложенный в public_html/las-viewer.</summary>
    public const string DefaultServerUrl = "https://amk-horizon.ru/las-viewer/LlmController.php";

    /// <summary>
    /// Ключ клиента, который сайт ждёт от «Просмотрщика LAS» (LlmController.DEFAULT_CLIENT_KEY).
    /// Не секрет: он отсекает случайные обращения, а расход ограничивает сайт.
    /// </summary>
    public const string BuiltInClientKey = "lasviewer_d9e85fbec598620ac7e99d777aa41f6d";

    /// <summary>
    /// Модель по умолчанию — первая из больших: на запросах с инструментами
    /// работает стабильнее остальных. Её получает пользователь, который ещё
    /// не выбирал модель сам (выбор сохраняется только по щелчку в списке).
    /// </summary>
    public const string DefaultModel = "google/gemini-3.8-flash";

    /// <summary>
    /// Модели агента в том порядке, в каком их видит пользователь: сначала
    /// большие, затем малые. Все — из каталога polza.ai с поддержкой вызова
    /// инструментов. Другие модели программа не предлагает: DeepSeek V3.2 Exp
    /// устарела, а Qwen3.8 Flash отклонял полные запросы на стороне
    /// провайдера (TOO_MANY_REQUESTS).
    /// </summary>
    public static IReadOnlyList<AgentModel> Models { get; } =
    [
        new(DefaultModel, "Gemini 3.8 Flash", Large: true),
        new("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash", Large: true),
        new("anthropic/claude-haiku-5.5", "Claude Haiku 5.5", Large: false),
        new("openai/gpt-6-luna-pro", "GPT-6 Luna Pro", Large: false)
    ];

    /// <summary>Модель из списка; сохранённая раньше модель, которой в списке нет (в том числе «модель сайта»), заменяется моделью по умолчанию.</summary>
    public static AgentModel ResolveModel(string? id) =>
        Models.FirstOrDefault(model => model.Id == id) ?? Models.First(model => model.Id == DefaultModel);

    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Идентификатор модели из <see cref="Models"/>.</summary>
    public string Model { get; set; } = DefaultModel;
}

/// <summary>Модель агента в списке выбора.</summary>
/// <param name="Large">Большая языковая модель (раздел «Большие языковые модели») или малая.</param>
public sealed record AgentModel(string Id, string Title, bool Large);

/// <summary>Настройки агента в профиле пользователя: <c>%LOCALAPPDATA%\AMK\LasViewer\agent-settings.json</c>.</summary>
public sealed class AgentSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = settingsPath;

    public AgentSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AgentSettings();
            }

            AgentSettings settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8), SerializerOptions)
                                     ?? new AgentSettings();
            settings.Model = AgentSettings.ResolveModel(settings.Model).Id;
            // Адрес сайта без файла посредника — от версий, ходивших через
            // front.php сайта horizon.ru; теперь посредник — отдельный файл.
            if (!settings.ServerUrl.Trim().TrimEnd('/').EndsWith(".php", StringComparison.OrdinalIgnoreCase)
                || settings.ServerUrl.Trim().TrimEnd('/').EndsWith("/front.php", StringComparison.OrdinalIgnoreCase))
            {
                settings.ServerUrl = AgentSettings.DefaultServerUrl;
            }

            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый файл настроек не должен мешать работе: модель
            // просто выбирается заново.
            return new AgentSettings();
        }
    }

    public bool TrySave(AgentSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions), new UTF8Encoding(false));
            File.Move(temporary, SettingsPath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
