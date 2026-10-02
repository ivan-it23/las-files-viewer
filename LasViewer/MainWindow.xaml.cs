using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AMK.LasViewer.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(3);

    private readonly EngineHost engine;
    private readonly Project project;
    private readonly DialogService dialogs;
    private readonly AutoUpdateService updateService = new();
    private readonly UpdateFlow updateFlow;
    private readonly CancellationTokenSource lifetime = new();
    private OpenedDocument? active;
    private bool engineStopped;
    private bool updatingScaleCombo;
    private bool opening;
    private readonly IReadOnlyList<string> startupPaths;

    public MainWindow(IReadOnlyList<string> startupPaths)
    {
        this.startupPaths = startupPaths;
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
        project = new Project(engine);
        Tablet.Dialogs = dialogs;

        AppWindow.Closing += AppWindow_Closing;
        RootGrid.Loaded += RootGrid_Loaded;
        ShowActiveDocument();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        if (await engine.StartAsync(lifetime.Token) && startupPaths.Count > 0)
        {
            await OpenPathsAsync(startupPaths);
        }

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

    // ------------------------------------------------------------------
    // Открытие и закрытие файлов
    // ------------------------------------------------------------------

    private async void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".las");
        picker.FileTypeFilter.Add("*");
        IReadOnlyList<StorageFile> files = await FilePickers.PickMultipleFilesAsync(picker, this, dialogs);
        await OpenPathsAsync(files.Select(file => file.Path).ToList());
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Открыть";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }

        // Папка открывается целиком: все LAS-файлы верхнего уровня — так
        // приходят данные прибора, где рядом лежат 400.las, 2000.las и сводный.
        List<string> paths = [];
        foreach (IStorageItem item in items)
        {
            if (item is StorageFolder folder)
            {
                paths.AddRange(Directory
                    .EnumerateFiles(folder.Path)
                    .Where(path => string.Equals(Path.GetExtension(path), ".las", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.CurrentCultureIgnoreCase));
            }
            else
            {
                paths.Add(item.Path);
            }
        }

        await OpenPathsAsync(paths);
    }

    private async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || opening)
        {
            return;
        }

        if (engine.State != EngineState.Ready)
        {
            await dialogs.ShowMessageAsync(
                "Движок не готов",
                "Файлы читает движок, а он сейчас не работает.\n\n"
                + (engine.LastError ?? "Дождитесь запуска движка или перезапустите его в меню «Движок»."));
            return;
        }

        opening = true;
        List<OpenFailure> failures = [];
        OpenedDocument? lastOpened = null;
        try
        {
            foreach (string path in paths)
            {
                if (project.Find(path) is { } existing)
                {
                    lastOpened = existing;
                    continue;
                }

                SetStatus($"Открытие {Path.GetFileName(path)}...");
                try
                {
                    lastOpened = await project.OpenAsync(path, lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (EngineException exception)
                {
                    AppLog.Write($"Не открыт {path}: {exception}");
                    failures.Add(new OpenFailure(path, Project.Describe(exception)));
                }
            }
        }
        finally
        {
            opening = false;
        }

        if (lastOpened is not null)
        {
            Activate(lastOpened);
            SetStatus($"Открыт {lastOpened.Document.Name}: {lastOpened.Document.RowCount:N0} строк, "
                      + $"{lastOpened.Document.Curves.Count - 1} кривых");
        }
        else
        {
            SetStatus(string.Empty);
        }

        if (failures.Count > 0)
        {
            string message = string.Join(
                Environment.NewLine + Environment.NewLine,
                failures.Select(failure => $"{Path.GetFileName(failure.Path)}\n{failure.Message}"));
            await dialogs.ShowMessageAsync(
                failures.Count == 1 ? "Файл не открыт" : $"Не открыто файлов: {failures.Count}",
                message);
        }
    }

    private async void CloseFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (active is null)
        {
            return;
        }

        OpenedDocument closing = active;
        int position = project.Documents.ToList().IndexOf(closing);
        active = null;
        await project.CloseAsync(closing);

        OpenedDocument? next = project.Documents.Count == 0
            ? null
            : project.Documents[Math.Min(position, project.Documents.Count - 1)];
        if (next is not null)
        {
            Activate(next);
        }
        else
        {
            ShowActiveDocument();
        }

        SetStatus($"Закрыт {closing.Document.Name}");
    }

    // ------------------------------------------------------------------
    // Проект и активный файл
    // ------------------------------------------------------------------

    private void Activate(OpenedDocument document)
    {
        SaveTabletView();
        active = document;
        ShowActiveDocument();
    }

    private void SaveTabletView()
    {
        if (active is not null && Tablet.HasData)
        {
            active.ViewTop = Tablet.Top;
            active.ViewPixelsPerUnit = Tablet.PixelsPerUnit;
        }
    }

    private void ShowActiveDocument()
    {
        RebuildProjectTree();
        CloseFileMenuItem.IsEnabled = active is not null;

        if (active is null)
        {
            Title = $"{AppInfo.ProductName} {AppInfo.VersionText}";
            Tablet.Clear();
            DataTable.Clear();
            HeaderPanel.Clear();
            SummaryPanel.Clear();
            UpdateViewVisibility();
            return;
        }

        LasDocument document = active.Document;
        Title = $"{document.Name} — {AppInfo.ProductName} {AppInfo.VersionText}";
        Tablet.Show(active.TabletData, active.Template, active.Annotations, document.FileId, active.ViewTop, active.ViewPixelsPerUnit);
        DataTable.Show(document);
        HeaderPanel.Show(document);
        SummaryPanel.Show(document);
        UpdateViewVisibility();
    }

    private void RebuildProjectTree()
    {
        ProjectTree.RootNodes.Clear();
        TreeViewNode? activeNode = null;
        foreach (OpenedDocument document in project.Documents)
        {
            TreeViewNode fileNode = new()
            {
                Content = new ProjectItem(document, null),
                IsExpanded = document == active
            };

            foreach (LasCurve curve in document.Document.DataCurves)
            {
                fileNode.Children.Add(new TreeViewNode { Content = new ProjectItem(document, curve) });
            }

            ProjectTree.RootNodes.Add(fileNode);
            if (document == active)
            {
                activeNode = fileNode;
            }
        }

        ProjectTree.SelectedNode = activeNode;
        ProjectEmptyText.Visibility = project.Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Кривую из дерева можно перетащить на трек планшета того же файла.</summary>
    private void ProjectTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        object? item = args.Items.FirstOrDefault();
        ProjectItem? projectItem = item switch
        {
            TreeViewNode { Content: ProjectItem node } => node,
            ProjectItem direct => direct,
            _ => null
        };

        if (projectItem?.Curve is null)
        {
            args.Cancel = true;
            return;
        }

        args.Data.Properties[Controls.LogTablet.DragPropertyKey] = $"curve|{projectItem.Document.Document.FileId}|{projectItem.Curve.Mnemonic}";
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void ProjectTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: ProjectItem item } && item.Document != active)
        {
            Activate(item.Document);
        }
    }

    // ------------------------------------------------------------------
    // Вкладки и планшет
    // ------------------------------------------------------------------

    private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UpdateViewVisibility();
    }

    private void UpdateViewVisibility()
    {
        string view = ViewSelector.SelectedItem?.Tag as string ?? "tablet";
        bool hasDocument = active is not null;

        Tablet.Visibility = Show(hasDocument && view == "tablet");
        DataTable.Visibility = Show(hasDocument && view == "data");
        HeaderPanel.Visibility = Show(hasDocument && view == "header");
        SummaryPanel.Visibility = Show(hasDocument && view == "summary");
        TabletToolbar.Visibility = Show(hasDocument && view == "tablet");
        EmptyHint.Visibility = Show(!hasDocument);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tablet_ViewChanged(object? sender, EventArgs e)
    {
        double ratio = Tablet.Ratio;
        ComboBoxItem? preset = ScaleCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is "fit"
                ? Tablet.IsShowingAll
                : double.TryParse(item.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                  && Math.Abs(value - ratio) / value < 0.005);

        // Подпись нужна только для масштаба не из списка — после Ctrl + колесо
        // или «Весь интервал»; для готового масштаба его показывает список.
        ScaleText.Text = (preset is null || preset.Tag is "fit") && ratio > 0
            ? "1:" + Math.Round(ratio).ToString("N0", CultureInfo.CurrentCulture)
            : string.Empty;

        updatingScaleCombo = true;
        ScaleCombo.SelectedItem = preset;
        updatingScaleCombo = false;

        if (active is not null)
        {
            active.ViewTop = Tablet.Top;
            active.ViewPixelsPerUnit = Tablet.PixelsPerUnit;
        }
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingScaleCombo || ScaleCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (tag == "fit")
        {
            Tablet.FitAll();
        }
        else if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
        {
            Tablet.SetRatio(ratio);
        }
    }

    // ------------------------------------------------------------------
    // Движок, обновления, справка
    // ------------------------------------------------------------------

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

    /// <summary>Узел дерева проекта: файл или его кривая.</summary>
    private sealed record ProjectItem(OpenedDocument Document, LasCurve? Curve)
    {
        public override string ToString()
        {
            if (Curve is not null)
            {
                return Curve.DisplayName;
            }

            return Document.Document.HasWarnings ? "⚠ " + Document.Document.Name : Document.Document.Name;
        }
    }
}
