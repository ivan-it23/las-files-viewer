using System.Globalization;
using AMK.LasViewer.Charting;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
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
/// Отрисовка — фигурами XAML. Кривые прорежены <see cref="CurveDecimator"/>
/// до четырёх точек на строку пикселей, поэтому число фигур не зависит от
/// длины записи. Вся математика (шкалы, метки, прореживание) — в
/// AMK.LasViewer.Charting; здесь только раскладка и ввод.
/// </remarks>
public sealed partial class LogTablet : UserControl
{
    private const double MinimumRatio = 10;
    private const double ZoomStep = 1.25;

    /// <summary>Уже этого трек не сжимается: подписи шкалы перестают читаться — включается прокрутка.</summary>
    private const double MinimumTrackWidth = 130;

    private static readonly SolidColorBrush TrackBorderBrush = new(Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8));
    private static readonly SolidColorBrush MajorGridBrush = new(Color.FromArgb(0xFF, 0xC4, 0xC4, 0xC4));
    private static readonly SolidColorBrush MinorGridBrush = new(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8));
    private static readonly SolidColorBrush CursorBrush = new(Color.FromArgb(0xC0, 0x20, 0x20, 0x20));
    private static readonly SolidColorBrush MutedTextBrush = new(Color.FromArgb(0xFF, 0x66, 0x66, 0x66));
    private static readonly SolidColorBrush HeaderBackground = new(Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));

    private readonly StackPanel headerPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel bodyPanel = new() { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Colors.White) };
    private readonly ScrollBar depthScrollBar = new()
    {
        Orientation = Orientation.Vertical,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
        IsEnabled = false
    };

    private readonly ScrollViewer horizontalScroller;
    private readonly List<TrackView> tracks = [];
    private DepthTrackView? depthTrack;
    private TabletData? data;
    private TabletTemplate? template;

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
            LayoutTrackWidths();
            ScheduleRedraw();
        };
        bodyPanel.PointerMoved += Body_PointerMoved;
        bodyPanel.PointerExited += Body_PointerExited;
        bodyPanel.PointerPressed += Body_PointerPressed;
        bodyPanel.PointerReleased += Body_PointerReleased;
        bodyPanel.PointerCaptureLost += (_, _) => dragging = false;
        bodyPanel.PointerWheelChanged += Body_PointerWheelChanged;
        depthScrollBar.ValueChanged += DepthScrollBar_ValueChanged;
    }

    /// <summary>Сменился видимый участок или масштаб.</summary>
    public event EventHandler? ViewChanged;

    public double Top => top;

    public double PixelsPerUnit => pixelsPerUnit;

    public bool HasData => data is not null && data.Depths.Length > 0;

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
    public void Show(TabletData tabletData, TabletTemplate tabletTemplate, double? savedTop, double? savedPixelsPerUnit)
    {
        data = tabletData;
        template = tabletTemplate;
        cursorDepth = null;
        BuildTracks();

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
        tracks.Clear();
        depthTrack = null;
        headerPanel.Children.Clear();
        bodyPanel.Children.Clear();
        depthScrollBar.IsEnabled = false;
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
        CommitView();
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
        CommitView();
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

        top = ClampTop(top);
        UpdateScrollBar();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CommitView()
    {
        top = ClampTop(top);
        UpdateScrollBar();
        ScheduleRedraw();
        ViewChanged?.Invoke(this, EventArgs.Empty);
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

    private void Body_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (data is null || bodyHeight <= 0)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(bodyPanel);
        double notches = point.Properties.MouseWheelDelta / 120.0;
        bool zoom = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

        if (zoom)
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

        CommitView();
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

        dragging = bodyPanel.CapturePointer(e.Pointer);
        dragStartY = point.Position.Y;
        dragStartTop = top;
        e.Handled = true;
    }

    private void Body_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            bodyPanel.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
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
            CommitView();
        }

        UpdateCursor(y);
    }

    private void Body_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (dragging)
        {
            return;
        }

        cursorDepth = null;
        UpdateCursorVisuals();
    }

    private void UpdateCursor(double y)
    {
        cursorDepth = y >= 0 && y <= bodyHeight ? top + y / pixelsPerUnit : null;
        UpdateCursorVisuals();
    }

    // ------------------------------------------------------------------
    // Построение и отрисовка
    // ------------------------------------------------------------------

    private void BuildTracks()
    {
        tracks.Clear();
        headerPanel.Children.Clear();
        bodyPanel.Children.Clear();

        depthTrack = new DepthTrackView(template!.DepthTrackWidth, data!.IndexUnit);
        headerPanel.Children.Add(depthTrack.Header);
        bodyPanel.Children.Add(depthTrack.Canvas);

        foreach (TrackDefinition definition in template.Tracks)
        {
            TrackView track = new(definition, data);
            tracks.Add(track);
            headerPanel.Children.Add(track.Header);
            bodyPanel.Children.Add(track.Canvas);
        }

        LayoutTrackWidths();
    }

    /// <summary>
    /// Треки делят доступную ширину пропорционально своим ширинам из шаблона.
    /// Если места не хватает, чтобы ни один трек не стал уже
    /// <see cref="MinimumTrackWidth"/>, треки получают ширины шаблона и
    /// появляется горизонтальная прокрутка.
    /// </summary>
    private void LayoutTrackWidths()
    {
        if (template is null || tracks.Count == 0 || availableWidth <= 0)
        {
            return;
        }

        double totalWeight = template.Tracks.Sum(track => track.WidthDip);
        double room = availableWidth - template.DepthTrackWidth - 1;
        double factor = room / totalWeight;
        if (template.Tracks.Any(track => track.WidthDip * factor < MinimumTrackWidth))
        {
            factor = 1;
        }

        foreach (TrackView track in tracks)
        {
            track.SetWidth(Math.Floor(track.Definition.WidthDip * factor));
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
            Redraw();
        });
    }

    private void Redraw()
    {
        if (data is null || depthTrack is null || bodyHeight <= 0 || pixelsPerUnit <= 0)
        {
            return;
        }

        DepthViewport viewport = new(top, pixelsPerUnit, bodyHeight);
        IReadOnlyList<DepthTick> ticks = DepthTicks.Generate(viewport);
        double majorStep = DepthTicks.MajorStep(viewport);

        depthTrack.Draw(viewport, ticks, majorStep);
        foreach (TrackView track in tracks)
        {
            track.Draw(viewport, ticks);
        }

        UpdateCursorVisuals();
    }

    private void UpdateCursorVisuals()
    {
        if (data is null || depthTrack is null)
        {
            return;
        }

        double? y = cursorDepth is { } depth ? (depth - top) * pixelsPerUnit : null;
        int row = cursorDepth is { } cursor ? data.NearestRow(cursor) : -1;

        depthTrack.ShowCursor(y, cursorDepth, data.Depths.Length > 1 ? data.Depths[1] - data.Depths[0] : 0.1);
        foreach (TrackView track in tracks)
        {
            track.ShowCursor(y, row);
        }
    }

    internal static string FormatValue(double value)
    {
        return double.IsNaN(value) ? "—" : value.ToString("G5", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------
    // Трек глубины
    // ------------------------------------------------------------------

    private sealed class DepthTrackView
    {
        private readonly double width;
        private readonly Line cursorLine = new() { Stroke = CursorBrush, StrokeThickness = 1 };
        private readonly Border cursorLabel = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
            Padding = new Thickness(3, 1, 3, 1),
            CornerRadius = new CornerRadius(2)
        };
        private readonly TextBlock cursorText = new() { FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };

        public DepthTrackView(double width, string unit)
        {
            this.width = width;
            cursorLabel.Child = cursorText;

            StackPanel title = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4) };
            title.Children.Add(new TextBlock
            {
                Text = "Глубина",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            title.Children.Add(new TextBlock
            {
                Text = unit.Length > 0 ? unit : "—",
                FontSize = 11,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            Header = new Border
            {
                Width = width,
                Background = HeaderBackground,
                BorderBrush = TrackBorderBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = title
            };
            Canvas = new Canvas { Width = width, Background = new SolidColorBrush(Colors.White) };
            Canvas.SizeChanged += (_, e) => Canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        }

        public Border Header { get; }

        public Canvas Canvas { get; }

        public void Draw(DepthViewport viewport, IReadOnlyList<DepthTick> ticks, double majorStep)
        {
            Canvas.Children.Clear();
            int decimals = majorStep >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(majorStep));

            GeometryGroup lines = new();
            foreach (DepthTick tick in ticks)
            {
                double y = viewport.ToY(tick.Depth);
                double length = tick.IsMajor ? 12 : 6;
                lines.Children.Add(new LineGeometry { StartPoint = new Point(width - length, y), EndPoint = new Point(width, y) });

                // Подпись у самого края обрезалась бы наполовину — она пропускается.
                if (tick.IsMajor && y >= 7 && y <= viewport.HeightDip - 7)
                {
                    TextBlock label = new()
                    {
                        Text = tick.Depth.ToString("F" + decimals, CultureInfo.CurrentCulture),
                        FontSize = 11,
                        Width = width - 16,
                        TextAlignment = TextAlignment.Right
                    };
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(label, y - 8);
                    Canvas.Children.Add(label);
                }
            }

            Canvas.Children.Insert(0, new Microsoft.UI.Xaml.Shapes.Path { Data = lines, Stroke = MutedTextBrush, StrokeThickness = 1 });
            Canvas.Children.Add(new Line
            {
                X1 = width - 0.5,
                X2 = width - 0.5,
                Y1 = 0,
                Y2 = viewport.HeightDip,
                Stroke = TrackBorderBrush,
                StrokeThickness = 1
            });
            Canvas.Children.Add(cursorLine);
            Canvas.Children.Add(cursorLabel);
        }

        public void ShowCursor(double? y, double? depth, double step)
        {
            if (y is not { } position || depth is not { } value)
            {
                cursorLine.Visibility = Visibility.Collapsed;
                cursorLabel.Visibility = Visibility.Collapsed;
                return;
            }

            int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(Math.Abs(step) > 0 ? Math.Abs(step) : 0.1)) + 1, 1, 4);
            cursorLine.X1 = 0;
            cursorLine.X2 = width;
            cursorLine.Y1 = cursorLine.Y2 = position;
            cursorText.Text = value.ToString("F" + decimals, CultureInfo.CurrentCulture);
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(cursorLabel, 2);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(cursorLabel, position - 9);
            cursorLine.Visibility = Visibility.Visible;
            cursorLabel.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------
    // Трек кривых
    // ------------------------------------------------------------------

    private sealed class TrackView
    {
        private readonly TrackDefinition definition;
        private readonly TabletData data;
        private double width;
        private readonly List<(CurveStyle Style, double[]? Values, TextBlock ValueText, SolidColorBrush Brush)> curves = [];
        private readonly Line cursorLine = new() { Stroke = CursorBrush, StrokeThickness = 1 };

        public TrackView(TrackDefinition definition, TabletData data)
        {
            this.definition = definition;
            this.data = data;
            width = definition.WidthDip;

            StackPanel rows = new() { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(6, 4, 6, 4), Spacing = 2 };
            foreach (CurveStyle style in definition.Curves)
            {
                SolidColorBrush brush = new(ToColor(style.Color));
                TextBlock valueText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                curves.Add((style, data.Values(style.Mnemonic), valueText, brush));
                rows.Children.Add(CurveHeader(style, brush, valueText));
            }

            Header = new Border
            {
                Width = definition.WidthDip,
                Background = HeaderBackground,
                BorderBrush = TrackBorderBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = rows
            };
            Canvas = new Canvas { Width = definition.WidthDip, Background = new SolidColorBrush(Colors.White) };
            Canvas.SizeChanged += (_, e) => Canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        }

        public Border Header { get; }

        public Canvas Canvas { get; }

        public TrackDefinition Definition => definition;

        public void SetWidth(double value)
        {
            width = value;
            Header.Width = value;
            Canvas.Width = value;
        }

        public void Draw(DepthViewport viewport, IReadOnlyList<DepthTick> ticks)
        {
            Canvas.Children.Clear();
            TrackScale scale = definition.Scale;

            GeometryGroup minor = new();
            GeometryGroup major = new();
            foreach (DepthTick tick in ticks)
            {
                double y = viewport.ToY(tick.Depth);
                (tick.IsMajor ? major : minor).Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(width, y) });
            }

            foreach (GridLine line in scale.GridLines(width))
            {
                double x = Math.Round(line.Fraction * width) + 0.5;
                (line.IsMajor ? major : minor).Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, viewport.HeightDip) });
            }

            Canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = minor, Stroke = MinorGridBrush, StrokeThickness = 1 });
            Canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = major, Stroke = MajorGridBrush, StrokeThickness = 1 });

            // Значение за пределами шкалы прижимается к краю трека, а не
            // исчезает: пользователь видит, что кривая ушла за шкалу.
            double ToX(double value)
            {
                double fraction = scale.ToFraction(value);
                return double.IsNaN(fraction) ? double.NaN : Math.Clamp(fraction, 0, 1) * width;
            }

            foreach ((CurveStyle style, double[]? values, _, SolidColorBrush brush) in curves)
            {
                if (values is null)
                {
                    continue;
                }

                foreach (PlotPoint[] segment in CurveDecimator.Build(data.Depths, values, viewport, ToX))
                {
                    PointCollection points = [];
                    foreach (PlotPoint point in segment)
                    {
                        points.Add(new Point(point.X, point.Y));
                    }

                    Canvas.Children.Add(new Polyline
                    {
                        Points = points,
                        Stroke = brush,
                        StrokeThickness = style.Thickness,
                        StrokeLineJoin = PenLineJoin.Round
                    });
                }
            }

            Canvas.Children.Add(new Line
            {
                X1 = width - 0.5,
                X2 = width - 0.5,
                Y1 = 0,
                Y2 = viewport.HeightDip,
                Stroke = TrackBorderBrush,
                StrokeThickness = 1
            });
            Canvas.Children.Add(cursorLine);
        }

        public void ShowCursor(double? y, int row)
        {
            if (y is not { } position)
            {
                cursorLine.Visibility = Visibility.Collapsed;
            }
            else
            {
                cursorLine.X1 = 0;
                cursorLine.X2 = width;
                cursorLine.Y1 = cursorLine.Y2 = position;
                cursorLine.Visibility = Visibility.Visible;
            }

            foreach ((_, double[]? values, TextBlock valueText, _) in curves)
            {
                valueText.Text = row >= 0 && values is not null ? FormatValue(values[row]) : string.Empty;
            }
        }

        private UIElement CurveHeader(CurveStyle style, SolidColorBrush brush, TextBlock valueText)
        {
            Grid grid = new() { ColumnSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(new Rectangle { Width = 16, Height = 2.5, Fill = brush, VerticalAlignment = VerticalAlignment.Center });

            TextBlock name = new()
            {
                Text = style.Mnemonic,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            Grid scaleRow = new();
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            scaleRow.Children.Add(ScaleText(FormatScale(definition.Scale.Left), HorizontalAlignment.Left, 0));
            scaleRow.Children.Add(ScaleText(data.Values(style.Mnemonic) is null ? "нет в файле" : UnitText(style.Mnemonic), HorizontalAlignment.Center, 1));
            scaleRow.Children.Add(ScaleText(FormatScale(definition.Scale.Right), HorizontalAlignment.Right, 2));
            Grid.SetRow(scaleRow, 1);
            Grid.SetColumnSpan(scaleRow, 3);
            grid.Children.Add(scaleRow);

            // Шкала рисуется линией цвета кривой — как подпись шкалы на бумажном планшете.
            Border underline = new()
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid,
                Padding = new Thickness(0, 0, 0, 1)
            };
            return underline;
        }

        private string UnitText(string mnemonic)
        {
            string unit = data.Unit(mnemonic);
            return definition.Scale.Kind == ScaleKind.Logarithmic
                ? (unit.Length > 0 ? unit + ", лог." : "лог.")
                : unit;
        }

        private static TextBlock ScaleText(string text, HorizontalAlignment alignment, int column)
        {
            TextBlock block = new()
            {
                Text = text,
                FontSize = 10,
                Foreground = MutedTextBrush,
                HorizontalAlignment = alignment
            };
            Grid.SetColumn(block, column);
            return block;
        }

        /// <summary>Граница шкалы без экспоненты: «10000», а не «1E+04».</summary>
        private static string FormatScale(double value) => value.ToString("0.####", CultureInfo.CurrentCulture);

        private static Color ToColor(uint argb) => Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
    }
}
