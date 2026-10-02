namespace AMK.LasViewer.App.Services;

/// <summary>
/// Журнал приложения и движка: один файл на день в
/// <c>%LOCALAPPDATA%\AMK\LasViewer\logs</c>.
/// </summary>
/// <remarks>
/// Отказ записи журнала не должен ронять приложение, поэтому ошибки ввода-вывода
/// здесь глотаются.
/// </remarks>
internal static class AppLog
{
    private static readonly object Sync = new();

    public static string CurrentFile => Path.Combine(
        AppInfo.LogDirectory,
        $"lasviewer-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.LogDirectory);
                File.AppendAllText(CurrentFile, line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
