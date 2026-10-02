using System.Diagnostics;
using System.Runtime.InteropServices;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Engine;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AMK.LasViewer.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(3);

    private readonly EngineHost engine;
    private readonly DialogService dialogs;
    private readonly AutoUpdateService updateService = new();
    private readonly UpdateFlow updateFlow;
    private readonly CancellationTokenSource lifetime = new();
    private bool engineStopped;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.ProductName} {AppInfo.VersionText}";
        SetInitialSize(1400, 900);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LasViewer.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        dialogs = new DialogService(() => Content.XamlRoot);
        updateFlow = new UpdateFlow(updateService, dialogs, SetStatus, Close);

        engine = new EngineHost(AppContext.BaseDirectory, AppInfo.VersionText);
        engine.LogLine += (_, line) => AppLog.Write(line);
        engine.StateChanged += (_, e) => DispatcherQueue.TryEnqueue(() => ShowEngineState(e));
        ShowEngineState(new EngineStateChangedEventArgs(EngineState.NotStarted, null));

        AppWindow.Closing += AppWindow_Closing;
        RootGrid.Loaded += RootGrid_Loaded;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        await engine.StartAsync(lifetime.Token);

        try
        {
            await Task.Delay(UpdateCheckDelay, lifetime.Token);
            await updateFlow.RunAsync(silent: true, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Закрытие откладывается до штатной остановки движка: он получает
    /// <c>system.shutdown</c> и успевает завершить начатое. Если движок завис,
    /// <see cref="EngineHost"/> завершает его принудительно.
    /// </summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (engineStopped)
        {
            return;
        }

        args.Cancel = true;
        lifetime.Cancel();
        SetStatus("Остановка движка...");
        try
        {
            await engine.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Ошибка остановки движка: {exception}");
        }

        updateService.Dispose();
        engineStopped = true;
        AppLog.Write("Приложение закрыто");
        Close();
    }

    private async void RestartEngineMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await engine.StartAsync(lifetime.Token);
    }

    private void OpenLogsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppInfo.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { AppInfo.LogDirectory } });
    }

    private async void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await updateFlow.RunAsync(silent: false, lifetime.Token);
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<string> lines =
        [
            $"{AppInfo.ProductName}, версия {AppInfo.VersionText}",
            "ООО НПФ «АМК ГОРИЗОНТ»",
            string.Empty
        ];

        if (engine.Info is { } info)
        {
            lines.Add($"Движок {info.EngineVersion}, протокол {info.ProtocolVersion}, PID {info.Pid}");
            lines.Add($"Python {info.Python.Version} {info.Python.Implementation}, "
                      + (info.Python.Embedded ? "встроенный" : "системный"));
            lines.Add(info.Python.Executable);
            string packages = string.Join(", ", info.Packages.Select(pair => $"{pair.Key} {pair.Value ?? "—"}"));
            lines.Add($"Пакеты: {packages}");
        }
        else
        {
            lines.Add($"Движок: {DescribeState(engine.State)}");
        }

        lines.Add(string.Empty);
        lines.Add($"Журналы: {AppInfo.LogDirectory}");
        lines.Add($"Обновления: {AutoUpdateService.UpdateRoot}");

        await dialogs.ShowMessageAsync("О программе", string.Join(Environment.NewLine, lines));
    }

    private async void EngineDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        await dialogs.ShowMessageAsync(
            "Движок не работает",
            (engine.LastError ?? "Причина не известна.")
            + Environment.NewLine + Environment.NewLine
            + "Попробуйте «Движок → Перезапустить движок». Подробности — в журнале: "
            + AppInfo.LogDirectory);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowEngineState(EngineStateChangedEventArgs change)
    {
        EngineIndicator.Fill = new SolidColorBrush(change.State switch
        {
            EngineState.Ready => Colors.ForestGreen,
            EngineState.Failed => Colors.Firebrick,
            EngineState.Starting => Colors.Goldenrod,
            _ => Colors.Gray
        });

        EngineStatusText.Text = change.State == EngineState.Ready && engine.Info is { } info
            ? $"Движок готов · Python {info.Python.Version} ({(info.Python.Embedded ? "встроенный" : "системный")})"
            : $"Движок: {DescribeState(change.State)}";
        EngineDetailsButton.Visibility = change.State == EngineState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeState(EngineState state)
    {
        return state switch
        {
            EngineState.NotStarted => "не запущен",
            EngineState.Starting => "запуск…",
            EngineState.Ready => "готов",
            EngineState.Failed => "ошибка",
            EngineState.Stopped => "остановлен",
            _ => state.ToString()
        };
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    /// <summary>
    /// Размер задаётся в эффективных пикселях, чтобы на мониторе 150 % окно не
    /// выглядело мелким, но не больше рабочей области: на ноутбуке 1366×768
    /// желаемый размер выходит за экран вместе со строкой состояния.
    /// </summary>
    private void SetInitialSize(int width, int height)
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        RectInt32 workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int actualWidth = Math.Min((int)(width * scale), workArea.Width * 95 / 100);
        int actualHeight = Math.Min((int)(height * scale), workArea.Height * 95 / 100);
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - actualWidth) / 2,
            workArea.Y + (workArea.Height - actualHeight) / 2,
            actualWidth,
            actualHeight));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
