namespace AMK.LasViewer.Charting;

/// <summary>
/// Готовая к рисованию картинка планшета: слои (трек глубины и треки кривых)
/// из простых элементов в DIP.
/// </summary>
/// <remarks>
/// Экран, экспорт в SVG и печать рисуют одну и ту же сцену. Поэтому то, что
/// пользователь видит на экране, и то, что уходит на бумагу, не расходятся:
/// расчёт шкал, заливок, переноса и прореживания выполняется в одном месте.
/// </remarks>
public sealed class TabletScene
{
    public List<SceneLayer> Layers { get; } = [];

    public double Width => Layers.Count == 0 ? 0 : Layers.Max(layer => layer.X + layer.Width);

    public double Height => Layers.Count == 0 ? 0 : Layers.Max(layer => layer.Y + layer.Height);
}

/// <summary>Прямоугольная область со своей системой координат и обрезкой по границам.</summary>
public sealed class SceneLayer(double x, double y, double width, double height)
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public List<ScenePrimitive> Items { get; } = [];
}

public abstract record ScenePrimitive;

/// <summary>Набор отрезков одного стиля — сетка, засечки. Рисуется одной фигурой.</summary>
public sealed record SceneSegments(IReadOnlyList<(PlotPoint From, PlotPoint To)> Segments, uint Color, double Thickness, LineDash Dash = LineDash.Solid)
    : ScenePrimitive;

public sealed record ScenePolyline(PlotPoint[] Points, uint Color, double Thickness, LineDash Dash) : ScenePrimitive;

public sealed record ScenePolygon(PlotPoint[] Points, uint Fill) : ScenePrimitive;

public sealed record SceneRect(double X, double Y, double Width, double Height, uint Fill, uint? Stroke = null) : ScenePrimitive;

public enum TextAnchor
{
    Left,
    Center,
    Right
}

/// <summary>
/// Подпись. <see cref="X"/> — точка привязки по <see cref="Anchor"/>,
/// <see cref="Y"/> — середина строки по высоте.
/// </summary>
public sealed record SceneText(
    double X,
    double Y,
    string Text,
    double Size,
    uint Color,
    TextAnchor Anchor = TextAnchor.Left,
    bool Bold = false,
    uint? Background = null) : ScenePrimitive;

public static class SceneColors
{
    public const uint Text = 0xFF202020;
    public const uint MutedText = 0xFF666666;
    public const uint Border = 0xFFB8B8B8;
    public const uint MajorGrid = 0xFFC4C4C4;
    public const uint MinorGrid = 0xFFE8E8E8;
    public const uint LabelBackground = 0xE6FFFFFF;

    public static uint WithAlpha(uint color, byte alpha) => (color & 0x00FFFFFF) | ((uint)alpha << 24);
}
