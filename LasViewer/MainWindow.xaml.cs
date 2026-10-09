using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    private readonly IReadOnlyList<string> startupPaths;
    private OpenedDocument? active;
    private bool engineStopped;
    private bool closeConfirmed;
    private bool updatingScaleCombo;
    private bool opening;

    public MainWindow(IReadOnlyList<string> startupPaths)
    {
        this.startupPaths = startupPaths;
        InitializeComponent();
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
        project.StateChanged += (_, _) => UpdateTitle();

        Panels.Dialogs = dialogs;
        Panels.FocusChanged += (_, _) =>
        {
            active = Panels.FocusedDocument ?? active;
            ShowActiveDocument();
        };
        Panels.FocusedViewChanged += (_, _) => UpdateScaleDisplay();
        Panels.Edited += (_, _) =>
        {
            project.MarkDirty();
            RebuildProjectTree();
        };
        Panels.LayoutChanged += (_, _) =>
        {
            SyncCheck.Visibility = Panels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            project.MarkDirty();
        };

        AgentPanel.Dialogs = dialogs;
        AgentPanel.Engine = engine;
        AgentPanel.ContextProvider = DescribeForAgent;

        AppWindow.Closing += AppWindow_Closing;
        RootGrid.Loaded += RootGrid_Loaded;
        RebuildTemplateMenu();
        ShowActiveDocument();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        if (await engine.StartAsync(lifetime.Token) && startupPaths.Count > 0)
        {
            string? projectFile = startupPaths.FirstOrDefault(path =>
                string.Equals(Path.GetExtension(path), Charting.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase));
            if (projectFile is not null)
            {
                await OpenProjectAsync(projectFile);
            }
            else
            {
                await OpenPathsAsync(startupPaths);
            }
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
    /// Закрытие: сначала вопрос о несохранённом проекте, затем штатная
    /// остановка движка — он получает <c>system.shutdown</c> и успевает
    /// завершить начатое. Если движок завис, <see cref="EngineHost"/>
    /// завершает его принудительно.
    /// </summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (engineStopped)
        {
            return;
        }

        args.Cancel = true;
        if (!closeConfirmed)
        {
            if (!await ConfirmDiscardChangesAsync())
            {
                return;
            }

            closeConfirmed = true;
        }

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
        // Перетаскивание кривых внутри окна обрабатывает планшет, не окно.
        if (e.DataView.Properties.ContainsKey(Controls.LogTablet.DragPropertyKey))
        {
            return;
        }

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

        string? projectFile = paths.FirstOrDefault(path =>
            string.Equals(Path.GetExtension(path), Charting.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase));
        if (projectFile is not null)
        {
            if (await ConfirmDiscardChangesAsync())
            {
                await OpenProjectAsync(projectFile);
            }

            return;
        }

        await OpenPathsAsync(paths);
    }

    private async Task<bool> EnsureEngineReadyAsync()
    {
        if (engine.State == EngineState.Ready)
        {
            return true;
        }

        await dialogs.ShowMessageAsync(
            "Движок не готов",
            "Файлы читает движок, а он сейчас не работает.\n\n"
            + (engine.LastError ?? "Дождитесь запуска движка или перезапустите его в меню «Движок»."));
        return false;
    }

    private async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || opening || !await EnsureEngineReadyAsync())
        {
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

        await ReportFailuresAsync(failures);
    }

    private async Task ReportFailuresAsync(IReadOnlyList<OpenFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        string message = string.Join(
            Environment.NewLine + Environment.NewLine,
            failures.Select(failure => $"{Path.GetFileName(failure.Path)}\n{failure.Message}"));
        await dialogs.ShowMessageAsync(failures.Count == 1 ? "Файл не открыт" : $"Не открыто файлов: {failures.Count}", message);
    }

    private async void CloseFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (active is not null)
        {
            await CloseDocumentAsync(active);
        }
    }

    private async Task CloseDocumentAsync(OpenedDocument closing)
    {
        int position = project.Documents.ToList().IndexOf(closing);
        Panels.Remove(closing);
        await project.CloseAsync(closing);

        if (active == closing)
        {
            active = Panels.FocusedDocument
                     ?? (project.Documents.Count == 0 ? null : project.Documents[Math.Clamp(position, 0, project.Documents.Count - 1)]);
            if (active is not null && Panels.Count == 0)
            {
                Panels.ShowSingle(active);
            }
        }

        ShowActiveDocument();
        SetStatus($"Закрыт {closing.Document.Name}");
    }

    // ------------------------------------------------------------------
    // Проект и активный файл
    // ------------------------------------------------------------------

    private void Activate(OpenedDocument document)
    {
        active = document;
        Panels.ShowSingle(document);
        ShowActiveDocument();
    }

    private void ShowActiveDocument()
    {
        RebuildProjectTree();
        CloseFileMenuItem.IsEnabled = active is not null;
        UpdateTitle();

        if (active is null)
        {
            DataTable.Clear();
            HeaderPanel.Clear();
            SummaryPanel.Clear();
            UpdateViewVisibility();
            return;
        }

        if (shownDetails != active)
        {
            shownDetails = active;
            DataTable.Show(active.Document);
            HeaderPanel.Show(active.Document);
            SummaryPanel.Show(active.Document);
        }

        UpdateViewVisibility();
        UpdateScaleDisplay();
    }

    private OpenedDocument? shownDetails;

    private void UpdateTitle()
    {
        string dirty = project.IsDirty && project.Documents.Count > 0 ? "*" : string.Empty;
        string file = active is null ? string.Empty : $" — {active.Document.Name}";
        Title = $"{project.DisplayName}{dirty}{file} — {AppInfo.ProductName} {AppInfo.VersionText}";
        ProjectTitle.Text = $"Проект: {project.DisplayName}{dirty}";
    }

    /// <summary>Состав дерева, по которому оно было построено в последний раз.</summary>
    private string? treeSignature;

    /// <summary>
    /// Обновляет дерево проекта. Узлы создаются заново, только если изменился
    /// состав: файлы, кривые или пометки. При смене активного файла меняются
    /// лишь выделение и раскрытие — так дерево не теряет прокрутку и не
    /// пересобирается на каждый щелчок.
    /// </summary>
    private void RebuildProjectTree()
    {
        string signature = TreeSignature();
        if (signature == treeSignature)
        {
            TreeViewNode? selected = null;
            foreach (TreeViewNode node in ProjectTree.RootNodes)
            {
                bool isActive = node.Content is ProjectItem { } item && item.Document == active;
                node.IsExpanded = isActive;
                if (isActive)
                {
                    selected = node;
                }
            }

            ProjectTree.SelectedNode = selected;
            return;
        }

        treeSignature = signature;
        ProjectTree.RootNodes.Clear();
        TreeViewNode? activeNode = null;
        foreach (OpenedDocument document in project.Documents)
        {
            TreeViewNode fileNode = new()
            {
                Content = new ProjectItem(document, null, null),
                IsExpanded = document == active
            };

            foreach (LasCurve curve in document.Document.DataCurves)
            {
                fileNode.Children.Add(new TreeViewNode { Content = new ProjectItem(document, curve, null) });
            }

            Charting.DocumentAnnotations notes = document.Annotations;
            if (!notes.IsEmpty)
            {
                TreeViewNode notesNode = new() { Content = new ProjectItem(document, null, "Пометки"), IsExpanded = true };
                foreach (Charting.DepthMarker marker in notes.Markers.OrderBy(marker => marker.Depth))
                {
                    notesNode.Children.Add(new TreeViewNode { Content = new ProjectItem(document, null, null, marker.Depth, $"◆ {Label(marker.Name, marker.Depth)}") });
                }

                foreach (Charting.DepthInterval interval in notes.Intervals.OrderBy(interval => interval.Top))
                {
                    string text = $"▭ {(interval.Name.Length > 0 ? interval.Name : "Интервал")} {interval.Top.ToString("0.##", CultureInfo.CurrentCulture)}–{interval.Bottom.ToString("0.##", CultureInfo.CurrentCulture)}";
                    notesNode.Children.Add(new TreeViewNode { Content = new ProjectItem(document, null, null, interval.Top, text) });
                }

                fileNode.Children.Add(notesNode);
            }

            ProjectTree.RootNodes.Add(fileNode);
            if (document == active)
            {
                activeNode = fileNode;
            }
        }

        ProjectTree.SelectedNode = activeNode;
        ProjectEmptyText.Visibility = project.Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        static string Label(string name, double depth) =>
            (name.Length > 0 ? name + " " : string.Empty) + depth.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Что сейчас открыто и видно — для системного сообщения агента: файлы с
    /// идентификаторами движка, активный файл, видимый интервал и курсор.
    /// Значения кривых сюда не входят — их агент получает инструментами.
    /// </summary>
    private string DescribeForAgent()
    {
        if (project.Documents.Count == 0)
        {
            return string.Empty;
        }

        CultureInfo invariant = CultureInfo.InvariantCulture;
        StringBuilder text = new("Открытые файлы:\n");
        foreach (OpenedDocument document in project.Documents)
        {
            LasDocument las = document.Document;
            Charting.TabletData data = document.TabletData;
            IEnumerable<string> curves = las.DataCurves
                .Take(40)
                .Select(curve => curve.Unit.Length > 0 ? $"{curve.Mnemonic} ({curve.Unit})" : curve.Mnemonic);
            text.Append("- ").Append(las.Name)
                .Append(" (file_id=").Append(las.FileId).Append(document == active ? ", активный" : string.Empty).Append("): ")
                .Append("глубина ").Append(data.Top.ToString("0.##", invariant)).Append('–').Append(data.Bottom.ToString("0.##", invariant))
                .Append(' ').Append(data.IndexUnit)
                .Append(", строк ").Append(las.RowCount.ToString(invariant))
                .Append(", кривые: ").Append(string.Join(", ", curves))
                .Append(las.DataCurves.Count() > 40 ? " и др." : string.Empty)
                .Append('\n');
        }

        if (Panels.FocusedTablet is { HasData: true } tablet && Panels.FocusedDocument is { } focused)
        {
            double top = tablet.Top;
            double bottom = tablet.Top + tablet.BodyHeight / tablet.PixelsPerUnit;
            text.Append("Видимый интервал планшета ").Append(focused.Document.Name).Append(": ")
                .Append(top.ToString("0.#", invariant)).Append('–').Append(bottom.ToString("0.#", invariant))
                .Append(' ').Append(focused.TabletData.IndexUnit).Append('\n');
            if (tablet.CursorDepth is { } cursor)
            {
                text.Append("Курсор: ").Append(cursor.ToString("0.##", invariant)).Append(' ').Append(focused.TabletData.IndexUnit).Append('\n');
            }
        }

        if (Panels.Count > 1)
        {
            text.Append("Рядом показаны: ").Append(string.Join(", ", Panels.Documents.Select(document => document.Document.Name))).Append('\n');
        }

        return text.ToString();
    }

    private string TreeSignature()
    {
        StringBuilder signature = new();
        foreach (OpenedDocument document in project.Documents)
        {
            signature.Append(document.Document.FileId).Append('|');
            foreach (LasCurve curve in document.Document.DataCurves)
            {
                signature.Append(curve.Mnemonic).Append(',');
            }

            foreach (Charting.DepthMarker marker in document.Annotations.Markers)
            {
                signature.Append('m').Append(marker.Depth.ToString("R", CultureInfo.InvariantCulture)).Append(marker.Name).Append(',');
            }

            foreach (Charting.DepthInterval interval in document.Annotations.Intervals)
            {
                signature.Append('i').Append(interval.Top.ToString("R", CultureInfo.InvariantCulture))
                    .Append('-').Append(interval.Bottom.ToString("R", CultureInfo.InvariantCulture)).Append(interval.Name).Append(',');
            }

            signature.Append(';');
        }

        return signature.ToString();
    }

    private void ProjectTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode { Content: ProjectItem item })
        {
            return;
        }

        // Переключение файла меняет узлы дерева. Делать это внутри события
        // самого дерева нельзя: TreeView ещё обрабатывает щелчок по узлу, и
        // при многократном переключении процесс падал с повреждением кучи
        // (0xc0000374 в ntdll) без управляемого исключения.
        DispatcherQueue.TryEnqueue(() => OpenTreeItem(item));
    }

    private void OpenTreeItem(ProjectItem item)
    {
        if (!project.Documents.Contains(item.Document))
        {
            return;
        }

        if (item.Document != active || !Panels.Contains(item.Document))
        {
            Activate(item.Document);
        }

        // Пометка: перейти к её глубине на планшете.
        if (item.Depth is { } depth)
        {
            Panels.FocusedTablet?.CenterOn(depth);
        }
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

    private void ProjectTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not TreeViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is not TreeViewItem container || ProjectTree.NodeFromContainer(container) is not { Content: ProjectItem item })
        {
            return;
        }

        OpenedDocument document = item.Document;
        MenuFlyout menu = new();
        MenuFlyoutItem show = new() { Text = "Показать на планшете" };
        show.Click += (_, _) => Activate(document);
        menu.Items.Add(show);

        MenuFlyoutItem beside = new() { Text = "Показать рядом", IsEnabled = !Panels.Contains(document) };
        beside.Click += (_, _) =>
        {
            Panels.Add(document);
            active = document;
            ShowActiveDocument();
        };
        menu.Items.Add(beside);

        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem close = new() { Text = $"Закрыть {document.Document.Name}" };
        close.Click += async (_, _) => await CloseDocumentAsync(document);
        menu.Items.Add(close);

        menu.ShowAt(container, new FlyoutShowOptions { Position = e.GetPosition(container) });
        e.Handled = true;
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

        Panels.Visibility = Show(hasDocument && view == "tablet");
        DataTable.Visibility = Show(hasDocument && view == "data");
        HeaderPanel.Visibility = Show(hasDocument && view == "header");
        SummaryPanel.Visibility = Show(hasDocument && view == "summary");
        TabletToolbar.Visibility = Show(hasDocument && view == "tablet");
        EmptyHint.Visibility = Show(!hasDocument);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateScaleDisplay()
    {
        Controls.LogTablet? tablet = Panels.FocusedTablet;
        if (tablet is null || !tablet.HasData)
        {
            ScaleText.Text = string.Empty;
            return;
        }

        double ratio = tablet.Ratio;
        ComboBoxItem? preset = ScaleCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is "fit"
                ? tablet.IsShowingAll
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
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingScaleCombo || ScaleCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || Panels.FocusedTablet is not { } tablet)
        {
            return;
        }

        if (tag == "fit")
        {
            tablet.FitAll();
        }
        else if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
        {
            tablet.SetRatio(ratio);
        }
    }

    private void SyncCheck_Click(object sender, RoutedEventArgs e)
    {
        Panels.SyncDepth = SyncCheck.IsChecked == true;
        project.MarkDirty();
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
        lines.Add($"Шаблоны: {TemplateLibrary.Directory}");
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

    /// <summary>Узел дерева проекта: файл, кривая, группа пометок или пометка.</summary>
    private sealed record ProjectItem(OpenedDocument Document, LasCurve? Curve, string? Group, double? Depth = null, string? Text = null)
    {
        public override string ToString()
        {
            if (Text is not null)
            {
                return Text;
            }

            if (Group is not null)
            {
                return Group;
            }

            if (Curve is not null)
            {
                return Curve.DisplayName;
            }

            return Document.Document.HasWarnings ? "⚠ " + Document.Document.Name : Document.Document.Name;
        }
    }
}
