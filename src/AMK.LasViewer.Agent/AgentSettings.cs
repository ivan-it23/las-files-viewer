using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AMK.LasViewer.Agent;

/// <summary>
/// Подключение к ИИ-агенту. По умолчанию ничего настраивать не нужно: сайт
/// компании подставляет свой ключ polza.ai и свою модель, а программа
/// предъявляет встроенный ключ клиента.
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
    /// Модели для быстрого выбора в панели агента; пустой идентификатор —
    /// модель сайта (deepseek/deepseek-v3.2-exp, как у ИИ-агента horizon.ru).
    /// Все — из каталога polza.ai с поддержкой вызова инструментов. Остальные
    /// модели каталога выбираются в настройках подключения.
    /// </summary>
    public static IReadOnlyList<(string Id, string Title)> QuickModels { get; } =
    [
        (string.Empty, "Модель сайта (DeepSeek V3.2 Exp)"),
        ("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash"),
        ("anthropic/claude-haiku-5.5", "Claude Haiku 5.5"),
        ("openai/gpt-6-luna-pro", "GPT-6 Luna Pro"),
        ("qwen/qwen3.8-flash", "Qwen3.8 Flash")
    ];

    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Модель; пусто — модель сайта (та же, что у ИИ-агента horizon.ru).</summary>
    public string Model { get; set; } = string.Empty;

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
