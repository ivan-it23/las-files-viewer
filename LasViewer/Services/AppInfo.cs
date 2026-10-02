using System.Reflection;
using System.Security.Principal;

namespace AMK.LasViewer.App.Services;

internal static class AppInfo
{
    public const string ProductName = "Просмотрщик LAS";

    public static Version Version { get; } = ReadVersion();

    public static string VersionText { get; } = Format(Version);

    /// <summary>
    /// Процесс работает с правами администратора. В таком режиме Windows не
    /// открывает системные диалоги выбора файлов — это видно по журналу сразу.
    /// </summary>
    public static bool IsElevatedProcess { get; } = DetectElevation();

    /// <summary>Каталог данных пользователя: журналы, настройки.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AMK",
        "LasViewer");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    public static string Format(Version version)
    {
        return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
    }

    private static Version ReadVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        Version version = Version.TryParse(informational, out Version? parsed)
            ? parsed
            : assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));
    }

    private static bool DetectElevation()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is SystemException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
