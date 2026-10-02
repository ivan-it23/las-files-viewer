using AMK.LasViewer.Charting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Переводит слой сцены планшета в фигуры XAML. Один и тот же код рисует
/// экран и страницы печати.
/// </summary>
internal static class SceneRenderer
{
    private static readonly Dictionary<uint, SolidColorBrush> Brushes = [];

    public static void Render(SceneLayer layer, Canvas canvas)
    {
        foreach (ScenePrimitive item in layer.Items)
        {
            UIElement? element = Create(item);
            if (element is not null)
            {
                canvas.Children.Add(element);
            }
        }
    }

    /// <summary>Холст слоя: размер слоя, обрезка по его границам, позиция внутри страницы.</summary>
    public static Canvas CreateLayerCanvas(SceneLayer layer)
    {
        Canvas canvas = new()
        {
            Width = layer.Width,
            Height = layer.Height,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, layer.Width, layer.Height) }
        };
        Canvas.SetLeft(canvas, layer.X);
        Canvas.SetTop(canvas, layer.Y);
        Render(layer, canvas);
        return canvas;
    }

    public static SolidColorBrush Brush(uint argb)
    {
        if (!Brushes.TryGetValue(argb, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ToColor(argb));
            Brushes[argb] = brush;
        }

        return brush;
    }

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static uint FromColor(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>Штрих в единицах толщины линии, как требует XAML.</summary>
    public static DoubleCollection? DashArray(LineDash dash) => dash switch
    {
        LineDash.Dash => [5, 2.5],
        LineDash.Dot => [1, 2],
        _ => null
    };

    private static UIElement? Create(ScenePrimitive item)
    {
        switch (item)
        {
            case SceneSegments segments when segments.Segments.Count > 0:
            {
                GeometryGroup group = new();
                foreach ((PlotPoint from, PlotPoint to) in segments.Segments)
                {
                    group.Children.Add(new LineGeometry { StartPoint = new Point(from.X, from.Y), EndPoint = new Point(to.X, to.Y) });
                }

                return new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = group,
                    Stroke = Brush(segments.Color),
                    StrokeThickness = segments.Thickness,
                    StrokeDashArray = DashArray(segments.Dash)
                };
            }

            case ScenePolyline polyline:
                return new Polyline
                {
                    Points = Points(polyline.Points),
                    Stroke = Brush(polyline.Color),
                    StrokeThickness = polyline.Thickness,
                    StrokeDashArray = DashArray(polyline.Dash),
                    StrokeLineJoin = PenLineJoin.Round
                };

            case ScenePolygon polygon:
                return new Polygon { Points = Points(polygon.Points), Fill = Brush(polygon.Fill) };

            case SceneRect rect:
            {
                Rectangle shape = new()
                {
                    Width = Math.Max(0, rect.Width),
                    Height = Math.Max(0, rect.Height),
                    Fill = (rect.Fill >> 24) == 0 ? null : Brush(rect.Fill)
                };
                if (rect.Stroke is { } stroke)
                {
                    shape.Stroke = Brush(stroke);
                    shape.StrokeThickness = 1;
                }

                Canvas.SetLeft(shape, rect.X);
                Canvas.SetTop(shape, rect.Y);
                return shape;
            }

            case SceneText text:
                return Text(text);

            default:
                return null;
        }
    }

    private static FrameworkElement Text(SceneText text)
    {
        TextBlock block = new()
        {
            Text = text.Text,
            FontSize = text.Size,
            Foreground = Brush(text.Color),
            FontWeight = text.Bold ? FontWeights.SemiBold : FontWeights.Normal
        };

        FrameworkElement element = text.Background is { } background
            ? new Border { Child = block, Background = Brush(background), Padding = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(2) }
            : block;

        // Привязка справа и по центру требует ширины текста — она известна
        // только после измерения.
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = element.DesiredSize;
        double left = text.Anchor switch
        {
            TextAnchor.Right => text.X - size.Width,
            TextAnchor.Center => text.X - size.Width / 2,
            _ => text.X - (text.Background is null ? 0 : 2)
        };
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, text.Y - size.Height / 2);
        return element;
    }

    private static PointCollection Points(PlotPoint[] points)
    {
        PointCollection collection = [];
        foreach (PlotPoint point in points)
        {
            collection.Add(new Point(point.X, point.Y));
        }

        return collection;
    }
}
