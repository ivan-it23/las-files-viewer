using System.Globalization;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Каротажный планшет: трек глубины и треки кривых по шаблону.
/// </summary>
/// <remarks>
/// Тело рисуется из сцены <see cref="TabletSceneBuilder"/> — той же, что уходит
/// в SVG и на печать. Здесь только раскладка, ввод, курсор и интерактивная
/// шапка (редактирование — в LogTablet.Editing.cs).
/// </remarks>
public sealed partial class LogTablet : UserControl
{
    private const double MinimumRatio = 10;
    private const double ZoomStep = 1.25;

    /// <summary>Уже этого трек не сжимается: подписи шкалы перестают читаться — включается прокрутка.</summary>
    private const double MinimumTrackWidth = 130;

    private static readonly SolidColorBrush CursorBrush = new(Color.FromArgb(0xC0, 0x20, 0x20, 0x20));
    private static readonly SolidColorBrush SelectionBrush = new(Color.FromArgb(0x40, 0x1F, 0x5F, 0xBF));

    private readonly StackPanel headerPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel bodyPanel = new() { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Colors.White) };
    private readonly ScrollBar depthScrollBar = new()
    {
        Orientation = Orientation.Vertical,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
        IsEnabled = false
    };

    private readonly ScrollViewer horizontalScroller;
    private readonly List<Canvas> layerCanvases = [];
    private readonly List<Line> cursorLines = [];
    private readonly List<Rectangle> selectionBands = [];
    private Border cursorLabel = new();
    private TextBlock cursorText = new();

    private TabletData? data;
    private TabletTemplate? template;
    private DocumentAnnotations? annotations;
    private string documentKey = string.Empty;
    private double[] trackWidths = [];

    private double top;
    private double pixelsPerUnit;
    private double bodyHeight;
    private double availableWidth;
    private bool fitPending;
    private double? requestedRatio;
    private double? cursorDepth;
    private bool redrawPending;
    private bool updatingScrollBar;

    private bool dragging;
    private double dragStartY;
    private double dragStartTop;
    private bool selecting;
    private double selectionStart;
    private double selectionEnd;

    public LogTablet()
    {
        Grid content = new();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(headerPanel);
        Grid.SetRow(bodyPanel, 1);
        content.Children.Add(bodyPanel);

        horizontalScroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled
        };

        Grid root = new();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(horizontalScroller);
        Grid.SetColumn(depthScrollBar, 1);
        root.Children.Add(depthScrollBar);
        Content = root;

        bodyPanel.SizeChanged += (_, e) =>
        {
            bodyHeight = e.NewSize.Height;
            ApplyPendingView();
            ScheduleRedraw();
        };
        horizontalScroller.SizeChanged += (_, e) =>
        {
            availableWidth = e.NewSize.Width;
            ScheduleRedraw();
        };
        bodyPanel.PointerMoved += Body_PointerMoved;
        bodyPanel.PointerExited += Body_PointerExited;
        bodyPanel.PointerPressed += Body_PointerPressed;
        bodyPanel.PointerReleased += Body_PointerReleased;
        bodyPanel.PointerCaptureLost += (_, _) =>
        {
            dragging = false;
            selecting = false;
            UpdateSelectionVisuals();
        };
        bodyPanel.PointerWheelChanged += Body_PointerWheelChanged;
        bodyPanel.RightTapped += Body_RightTapped;
        depthScrollBar.ValueChanged += DepthScrollBar_ValueChanged;
    }

    /// <summary>Сменился видимый участок или масштаб — по действию пользователя.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Курсор перемещён пользователем (null — ушёл с планшета).</summary>
    public event EventHandler<double?>? CursorDepthChanged;

    /// <summary>Изменён шаблон или пометки: проект нужно сохранить.</summary>
    public event EventHandler? Edited;

    /// <summary>Очередь диалогов окна; без неё редактирование недоступно.</summary>
    internal DialogService? Dialogs { get; set; }

    public double Top => top;

    public double PixelsPerUnit => pixelsPerUnit;

    public double BodyHeight => bodyHeight;

    public bool HasData => data is not null && data.Depths.Length > 0;

    public string IndexUnit => data?.IndexUnit ?? string.Empty;

    public IReadOnlyList<double> TrackWidths => trackWidths;

    /// <summary>Виден весь интервал записи — масштаб «Весь интервал».</summary>
    public bool IsShowingAll => data is not null && bodyHeight > 0 && pixelsPerUnit > 0
                                && Math.Abs(pixelsPerUnit - FitScale()) / FitScale() < 0.005;

    /// <summary>Текущий масштаб как знаменатель 1:N.</summary>
    public double Ratio => data is null || pixelsPerUnit <= 0
        ? 0
        : DepthViewport.RatioForPixelsPerUnit(pixelsPerUnit, data.IndexUnit);

    /// <summary>
    /// Показывает данные. Без сохранённого вида — масштаб 1:500 от начала
    /// записи: привычный масштаб, на котором кривые читаются.
    /// </summary>
    public void Show(
        TabletData tabletData,
        TabletTemplate tabletTemplate,
        DocumentAnnotations documentAnnotations,
        string key,
        double? savedTop,
        double? savedPixelsPerUnit)
    {
        data = tabletData;
        template = tabletTemplate;
        annotations = documentAnnotations;
        documentKey = key;
        cursorDepth = null;
        Rebuild();

        if (savedTop is { } restoredTop && savedPixelsPerUnit is { } restoredScale)
        {
            top = restoredTop;
            pixelsPerUnit = restoredScale;
            requestedRatio = null;
            fitPending = false;
        }
        else
        {
            requestedRatio = 500;
            fitPending = false;
            top = data.Top;
        }

        ApplyPendingView();
        ScheduleRedraw();
    }

    public void Clear()
    {
        data = null;
        template = null;
        annotations = null;
        headerPanel.Children.Clear();
        bodyPanel.Children.Clear();
        layerCanvases.Clear();
        cursorLines.Clear();
        selectionBands.Clear();
        headerValueTexts.Clear();
        depthScrollBar.IsEnabled = false;
    }

    /// <summary>Перестроить шапку и тело после изменения шаблона или пометок.</summary>
    public void Refresh()
    {
        if (data is null)
        {
            return;
        }

        Rebuild();
        ScheduleRedraw();
    }

    /// <summary>Устанавливает масштаб 1:<paramref name="ratio"/>, сохраняя глубину в центре окна.</summary>
    public void SetRatio(double ratio)
    {
        if (data is null)
        {
            return;
        }

        if (bodyHeight <= 0)
        {
            requestedRatio = ratio;
            return;
        }

        double center = top + bodyHeight / pixelsPerUnit / 2;
        pixelsPerUnit = ClampScale(DepthViewport.PixelsPerUnitForRatio(ratio, data.IndexUnit));
        top = center - bodyHeight / pixelsPerUnit / 2;
        CommitView(raise: true);
    }

    /// <summary>Весь интервал записи в высоту окна.</summary>
    public void FitAll()
    {
        if (data is null)
        {
            return;
        }

        if (bodyHeight <= 0)
        {
            fitPending = true;
            requestedRatio = null;
            return;
        }

        pixelsPerUnit = FitScale();
        top = data.Top;
        CommitView(raise: true);
    }

    /// <summary>Глубина <paramref name="depth"/> — в центр окна, масштаб не меняется.</summary>
    public void CenterOn(double depth)
    {
        if (data is null || bodyHeight <= 0 || pixelsPerUnit <= 0)
        {
            return;
        }

        top = depth - bodyHeight / pixelsPerUnit / 2;
        CommitView(raise: true);
    }

    /// <summary>Вид, заданный другим планшетом при синхронизации; событие не поднимается.</summary>
    public void SetView(double newTop, double newPixelsPerUnit)
    {
        if (data is null || bodyHeight <= 0)
        {
            return;
        }

        pixelsPerUnit = ClampScale(newPixelsPerUnit);
        top = newTop;
        CommitView(raise: false);
    }

    /// <summary>Курсор другого планшета; событие не поднимается.</summary>
    public void ShowExternalCursor(double? depth)
    {
        cursorDepth = depth;
        UpdateCursorVisuals();
    }

    private void ApplyPendingView()
    {
        if (data is null || bodyHeight <= 0)
        {
            return;
        }

        if (fitPending)
        {
            fitPending = false;
            pixelsPerUnit = FitScale();
            top = data.Top;
        }
        else if (requestedRatio is { } ratio)
        {
            requestedRatio = null;
            pixelsPerUnit = ClampScale(DepthViewport.PixelsPerUnitForRatio(ratio, data.IndexUnit));
        }
        else if (pixelsPerUnit > 0)
        {
            pixelsPerUnit = ClampScale(pixelsPerUnit);
        }

        top = ClampTop(top);
        UpdateScrollBar();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CommitView(bool raise)
    {
        top = ClampTop(top);
        UpdateScrollBar();
        ScheduleRedraw();
        if (raise)
        {
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double FitScale()
    {
        double span = Math.Max(data!.Bottom - data.Top, 1e-6);
        return bodyHeight / span;
    }

    private double ClampScale(double value)
    {
        double maximum = DepthViewport.PixelsPerUnitForRatio(MinimumRatio, data!.IndexUnit);
        double minimum = Math.Min(FitScale(), maximum);
        return Math.Clamp(value, minimum, maximum);
    }

    private double ClampTop(double value)
    {
        if (data is null || pixelsPerUnit <= 0 || bodyHeight <= 0)
        {
            return value;
        }

        double span = bodyHeight / pixelsPerUnit;
        double maximumTop = Math.Max(data.Top, data.Bottom - span);
        return Math.Clamp(value, data.Top, maximumTop);
    }

    private void UpdateScrollBar()
    {
        if (data is null || bodyHeight <= 0 || pixelsPerUnit <= 0)
        {
            depthScrollBar.IsEnabled = false;
            return;
        }

        double span = bodyHeight / pixelsPerUnit;
        updatingScrollBar = true;
        depthScrollBar.Minimum = data.Top;
        depthScrollBar.Maximum = Math.Max(data.Top, data.Bottom - span);
        depthScrollBar.ViewportSize = span;
        depthScrollBar.LargeChange = span * 0.9;
        depthScrollBar.SmallChange = span * 0.1;
        depthScrollBar.Value = top;
        depthScrollBar.IsEnabled = depthScrollBar.Maximum > depthScrollBar.Minimum;
        updatingScrollBar = false;
    }

    private void DepthScrollBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updatingScrollBar || data is null)
        {
            return;
        }

        top = ClampTop(e.NewValue);
        ScheduleRedraw();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // Ввод
    // ------------------------------------------------------------------

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Body_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (data is null || bodyHeight <= 0)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(bodyPanel);
        double notches = point.Properties.MouseWheelDelta / 120.0;

        if (IsKeyDown(VirtualKey.Control))
        {
            // Масштаб меняется вокруг глубины под курсором: точка, на которую
            // смотрит пользователь, остаётся на месте.
            double anchor = top + point.Position.Y / pixelsPerUnit;
            pixelsPerUnit = ClampScale(pixelsPerUnit * Math.Pow(ZoomStep, notches));
            top = anchor - point.Position.Y / pixelsPerUnit;
        }
        else
        {
            top -= notches * bodyHeight / pixelsPerUnit * 0.1;
        }

        CommitView(raise: true);
        UpdateCursor(point.Position.Y);
        e.Handled = true;
    }

    private void Body_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(bodyPanel);
        if (data is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!bodyPanel.CapturePointer(e.Pointer))
        {
            return;
        }

        if (IsKeyDown(VirtualKey.Shift) && Dialogs is not null)
        {
            // Shift + перетаскивание выделяет интервал.
            selecting = true;
            selectionStart = selectionEnd = top + point.Position.Y / pixelsPerUnit;
            UpdateSelectionVisuals();
        }
        else
        {
            dragging = true;
            dragStartY = point.Position.Y;
            dragStartTop = top;
        }

        e.Handled = true;
    }

    private async void Body_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            bodyPanel.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!selecting)
        {
            return;
        }

        selecting = false;
        bodyPanel.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        double from = Math.Min(selectionStart, selectionEnd);
        double to = Math.Max(selectionStart, selectionEnd);
        UpdateSelectionVisuals();

        // Случайный щелчок с Shift — не интервал.
        if ((to - from) * pixelsPerUnit >= 4)
        {
            await CreateIntervalAsync(from, to);
        }
    }

    private void Body_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (data is null)
        {
            return;
        }

        double y = e.GetCurrentPoint(bodyPanel).Position.Y;
        if (dragging)
        {
            top = dragStartTop - (y - dragStartY) / pixelsPerUnit;
            CommitView(raise: true);
        }
        else if (selecting)
        {
            selectionEnd = top + Math.Clamp(y, 0, bodyHeight) / pixelsPerUnit;
            UpdateSelectionVisuals();
        }

        UpdateCursor(y);
    }

    private void Body_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (dragging || selecting)
        {
            return;
        }

        cursorDepth = null;
        UpdateCursorVisuals();
        CursorDepthChanged?.Invoke(this, null);
    }

    private void UpdateCursor(double y)
    {
        cursorDepth = y >= 0 && y <= bodyHeight ? top + y / pixelsPerUnit : null;
        UpdateCursorVisuals();
        CursorDepthChanged?.Invoke(this, cursorDepth);
    }

    // ------------------------------------------------------------------
    // Построение и отрисовка
    // ------------------------------------------------------------------

    private void Rebuild()
    {
        BuildHeaders();

        bodyPanel.Children.Clear();
        layerCanvases.Clear();
        cursorLines.Clear();
        selectionBands.Clear();
        // Элементы курсора создаются заново вместе с холстами: элемент XAML,
        // оставшийся дочерним у старого холста, нельзя добавить в новый, а
        // FrameworkElement.Parent у вынутого из дерева холста не сообщается.
        cursorText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
        cursorLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
            Padding = new Thickness(3, 1, 3, 1),
            CornerRadius = new CornerRadius(2),
            Child = cursorText,
            Visibility = Visibility.Collapsed
        };

        int layers = template!.Tracks.Count + 1;
        for (int index = 0; index < layers; index++)
        {
            Canvas canvas = new() { Background = new SolidColorBrush(Colors.White) };
            canvas.SizeChanged += (_, e) => canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            if (index > 0)
            {
                AttachTrackDropTarget(canvas, index - 1);
            }

            layerCanvases.Add(canvas);
            bodyPanel.Children.Add(canvas);
            cursorLines.Add(new Line { Stroke = CursorBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed });
            selectionBands.Add(new Rectangle { Fill = SelectionBrush, Visibility = Visibility.Collapsed });
        }

        ApplyWidths();
    }

    private void ApplyWidths()
    {
        if (template is null)
        {
            return;
        }

        trackWidths = availableWidth > 0
            ? TabletSceneBuilder.FitTrackWidths(template, availableWidth, MinimumTrackWidth)
            : template.Tracks.Select(track => track.WidthDip).ToArray();

        for (int index = 0; index < layerCanvases.Count; index++)
        {
            double width = index == 0 ? template.DepthTrackWidth : trackWidths[index - 1];
            layerCanvases[index].Width = width;
            if (index < headerBorders.Count)
            {
                headerBorders[index].Width = width;
            }
        }
    }

    private void ScheduleRedraw()
    {
        if (redrawPending)
        {
            return;
        }

        redrawPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            redrawPending = false;
            // Исключение в обратном вызове диспетчера не доходит до
            // App.UnhandledException и закрывает процесс вместе с несохранённой
            // работой. Сбой отрисовки пишется в журнал, окно остаётся.
            try
            {
                Redraw();
            }
            catch (Exception exception)
            {
                AppLog.Write($"Сбой отрисовки планшета: {exception}");
            }
        });
    }

    private void Redraw()
    {
        if (data is null || template is null || bodyHeight <= 0 || pixelsPerUnit <= 0 || layerCanvases.Count == 0)
        {
            return;
        }

        ApplyWidths();
        DepthViewport viewport = new(top, pixelsPerUnit, bodyHeight);
        TabletScene scene = TabletSceneBuilder.BuildBody(data, template, trackWidths, viewport, annotations);

        for (int index = 0; index < layerCanvases.Count && index < scene.Layers.Count; index++)
        {
            Canvas canvas = layerCanvases[index];
            canvas.Children.Clear();
            SceneRenderer.Render(scene.Layers[index], canvas);
            canvas.Children.Add(selectionBands[index]);
            canvas.Children.Add(cursorLines[index]);
        }

        layerCanvases[0].Children.Add(cursorLabel);
        UpdateCursorVisuals();
        UpdateSelectionVisuals();
    }

    private void UpdateCursorVisuals()
    {
        if (data is null || layerCanvases.Count == 0)
        {
            return;
        }

        double? y = cursorDepth is { } depth && pixelsPerUnit > 0 ? (depth - top) * pixelsPerUnit : null;
        bool visible = y is { } position && position >= 0 && position <= bodyHeight;
        for (int index = 0; index < cursorLines.Count; index++)
        {
            Line line = cursorLines[index];
            line.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                line.X1 = 0;
                line.X2 = layerCanvases[index].Width;
                line.Y1 = line.Y2 = y!.Value;
            }
        }

        cursorLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            double step = data.Depths.Length > 1 ? Math.Abs(data.Depths[1] - data.Depths[0]) : 0.1;
            int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step > 0 ? step : 0.1)) + 1, 1, 4);
            cursorText.Text = cursorDepth!.Value.ToString("F" + decimals, CultureInfo.CurrentCulture);
            Canvas.SetLeft(cursorLabel, 2);
            Canvas.SetTop(cursorLabel, y!.Value - 9);
        }

        int row = visible ? data.NearestRow(cursorDepth!.Value) : -1;
        UpdateHeaderValues(row);
    }

    private void UpdateSelectionVisuals()
    {
        bool visible = selecting && data is not null;
        double from = (Math.Min(selectionStart, selectionEnd) - top) * pixelsPerUnit;
        double to = (Math.Max(selectionStart, selectionEnd) - top) * pixelsPerUnit;
        for (int index = 0; index < selectionBands.Count; index++)
        {
            Rectangle band = selectionBands[index];
            band.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                band.Width = layerCanvases[index].Width;
                band.Height = Math.Max(1, to - from);
                Canvas.SetTop(band, from);
            }
        }
    }

    internal static string FormatValue(double value)
    {
        return double.IsNaN(value) ? "—" : value.ToString("G5", CultureInfo.CurrentCulture);
    }
}
