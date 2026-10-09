using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Модель по умолчанию — первая из больших: проверена на запросах с инструментами и самая недорогая.</summary>
    public const string DefaultModel = "deepseek/deepseek-v4-flash";

    /// <summary>
    /// Модели агента в том порядке, в каком их видит пользователь: сначала
    /// большие, затем малые. Все — из каталога polza.ai с поддержкой вызова
    /// инструментов. Другие модели программа не предлагает: DeepSeek V3.2 Exp
    /// устарела, а Qwen3.8 Flash отклонял полные запросы на стороне
    /// провайдера (TOO_MANY_REQUESTS).
    /// </summary>
    public static IReadOnlyList<AgentModel> Models { get; } =
    [
        new(DefaultModel, "DeepSeek V4 Flash", "Сложный анализ нескольких файлов, скрипты и графики. Самая недорогая.", Large: true),
        new("google/gemini-3.8-flash", "Gemini 3.8 Flash", "Новая флагманская модель: сложный анализ, скрипты, графики и отчёты.", Large: true),
        new("anthropic/claude-haiku-5.5", "Claude Haiku 5.5", "Быстрые ответы на простые вопросы: сведения о файле, статистика кривых.", Large: false),
        new("openai/gpt-6-luna-pro", "GPT-6 Luna Pro", "Простые задачи: поиск интервалов, пропусков, сравнение двух кривых.", Large: false)
    ];

    /// <summary>Модель из списка; сохранённая раньше модель, которой в списке нет (в том числе «модель сайта»), заменяется моделью по умолчанию.</summary>
    public static AgentModel ResolveModel(string? id) =>
        Models.FirstOrDefault(model => model.Id == id) ?? Models.First(model => model.Id == DefaultModel);

    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Идентификатор модели из <see cref="Models"/>.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// Необязательный персональный токен из «Профиля» — для учёта расхода по
    /// сотруднику. Зашифрован DPAPI: скопированный на другой компьютер файл
    /// настроек расшифровать нельзя.
    /// </summary>
    public string ProtectedToken { get; set; } = string.Empty;

    /// <summary>Токен в открытом виде — только в памяти.</summary>
    [JsonIgnore]
    public string Token { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl);

    public AgentSettings Clone() => new()
    {
        ServerUrl = ServerUrl,
        Model = Model,
        ProtectedToken = ProtectedToken,
        Token = Token
    };
}

/// <summary>Модель агента в списке выбора.</summary>
/// <param name="Description">Для каких задач модель — пояснение пользователю.</param>
/// <param name="Large">Большая языковая модель (сложные задачи) или малая (простые).</param>
public sealed record AgentModel(string Id, string Title, string Description, bool Large);

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
            settings.Token = DataProtection.TryUnprotect(settings.ProtectedToken);
            settings.Model = AgentSettings.ResolveModel(settings.Model).Id;
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый файл настроек не должен мешать работе: токен
            // просто вводится заново.
            return new AgentSettings();
        }
    }

    public bool TrySave(AgentSettings settings)
    {
        try
        {
            settings.ProtectedToken = DataProtection.Protect(settings.Token);
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

/// <summary>Шифрование токена средствами Windows (DPAPI) для текущего пользователя.</summary>
internal static class DataProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        DataBlob input = Allocate(Encoding.UTF8.GetBytes(value));
        DataBlob output = default;
        try
        {
            return CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                ? Convert.ToBase64String(Copy(output))
                : string.Empty;
        }
        finally
        {
            Release(ref input);
            Free(ref output);
        }
    }

    public static string TryUnprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        DataBlob input = Allocate(encrypted);
        DataBlob output = default;
        try
        {
            return CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                ? Encoding.UTF8.GetString(Copy(output))
                : string.Empty;
        }
        finally
        {
            Release(ref input);
            Free(ref output);
        }
    }

    private static DataBlob Allocate(byte[] data)
    {
        IntPtr pointer = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { DataLength = data.Length, DataPointer = pointer };
    }

    private static byte[] Copy(DataBlob blob)
    {
        byte[] result = new byte[blob.DataLength];
        Marshal.Copy(blob.DataPointer, result, 0, blob.DataLength);
        return result;
    }

    private static void Release(ref DataBlob blob)
    {
        if (blob.DataPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.DataPointer);
            blob = default;
        }
    }

    /// <summary>Результат CryptProtectData выделяется через LocalAlloc и освобождается LocalFree.</summary>
    private static void Free(ref DataBlob blob)
    {
        if (blob.DataPointer != IntPtr.Zero)
        {
            LocalFree(blob.DataPointer);
            blob = default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int DataLength;
        public IntPtr DataPointer;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
