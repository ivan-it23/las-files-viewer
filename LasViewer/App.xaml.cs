using AMK.LasViewer.App.Services;
using Microsoft.UI.Xaml;

namespace AMK.LasViewer.App;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write($"Необработанное исключение: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppLog.Write(
                $"Запуск {AppInfo.ProductName} {AppInfo.VersionText}, каталог {AppContext.BaseDirectory}, "
                + $"права администратора: {AppInfo.IsElevatedProcess}");
            // Пути из командной строки открываются после запуска движка: так
            // работает открытие файла двойным щелчком и из других программ.
            string[] paths = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            window = new MainWindow(paths);
            window.Activate();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Сбой запуска: {exception}");
            throw;
        }
    }
}
