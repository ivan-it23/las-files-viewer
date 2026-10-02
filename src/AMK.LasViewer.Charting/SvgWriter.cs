using System.Globalization;
using System.Security;
using System.Text;

namespace AMK.LasViewer.Charting;

/// <summary>Записывает сцену планшета в SVG — векторный формат, который открывают браузеры, Word и Inkscape.</summary>
public static class SvgWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Write(TabletScene scene, double width, double height, string title)
    {
        StringBuilder svg = new();
        svg.Append(Invariant, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"0 0 {N(width)} {N(height)}\">\n");
        svg.Append("<title>").Append(SecurityElement.Escape(title)).Append("</title>\n");
        svg.Append(Invariant, $"<rect width=\"{N(width)}\" height=\"{N(height)}\" fill=\"#FFFFFF\"/>\n");
        svg.Append("<g font-family=\"Segoe UI, Arial, sans-serif\">\n");

        int clipIndex = 0;
        foreach (SceneLayer layer in scene.Layers)
        {
            string clipId = $"c{clipIndex++}";
            svg.Append(Invariant, $"<clipPath id=\"{clipId}\"><rect width=\"{N(layer.Width)}\" height=\"{N(layer.Height)}\"/></clipPath>\n");
            svg.Append(Invariant, $"<g transform=\"translate({N(layer.X)},{N(layer.Y)})\" clip-path=\"url(#{clipId})\">\n");
            foreach (ScenePrimitive item in layer.Items)
            {
                WriteItem(svg, item);
            }

            svg.Append("</g>\n");
        }

        svg.Append("</g>\n</svg>\n");
        return svg.ToString();
    }

    private static void WriteItem(StringBuilder svg, ScenePrimitive item)
    {
        switch (item)
        {
            case SceneSegments segments when segments.Segments.Count > 0:
            {
                svg.Append("<path d=\"");
                foreach ((PlotPoint from, PlotPoint to) in segments.Segments)
                {
                    svg.Append(Invariant, $"M{N(from.X)} {N(from.Y)}L{N(to.X)} {N(to.Y)}");
                }

                svg.Append("\" fill=\"none\"").Append(Stroke(segments.Color, segments.Thickness, segments.Dash)).Append("/>\n");
                break;
            }

            case ScenePolyline polyline:
                svg.Append("<polyline points=\"").Append(Points(polyline.Points)).Append("\" fill=\"none\" stroke-linejoin=\"round\"")
                    .Append(Stroke(polyline.Color, polyline.Thickness, polyline.Dash)).Append("/>\n");
                break;

            case ScenePolygon polygon:
                svg.Append("<polygon points=\"").Append(Points(polygon.Points)).Append('"').Append(Fill(polygon.Fill)).Append("/>\n");
                break;

            case SceneRect rect:
                svg.Append(Invariant, $"<rect x=\"{N(rect.X)}\" y=\"{N(rect.Y)}\" width=\"{N(rect.Width)}\" height=\"{N(rect.Height)}\"")
                    .Append(Fill(rect.Fill));
                if (rect.Stroke is { } stroke)
                {
                    svg.Append(Stroke(stroke, 1, LineDash.Solid));
                }

                svg.Append("/>\n");
                break;

            case SceneText text:
                WriteText(svg, text);
                break;
        }
    }

    private static void WriteText(StringBuilder svg, SceneText text)
    {
        if (text.Background is { } background)
        {
            // Ширина подложки — оценка: точная ширина текста известна только при отрисовке.
            double width = text.Text.Length * text.Size * 0.58 + 4;
            double x = text.Anchor switch
            {
                TextAnchor.Center => text.X - width / 2,
                TextAnchor.Right => text.X - width,
                _ => text.X - 2
            };
            svg.Append(Invariant, $"<rect x=\"{N(x)}\" y=\"{N(text.Y - text.Size * 0.7)}\" width=\"{N(width)}\" height=\"{N(text.Size * 1.4)}\"")
                .Append(Fill(background)).Append("/>\n");
        }

        string anchor = text.Anchor switch
        {
            TextAnchor.Center => "middle",
            TextAnchor.Right => "end",
            _ => "start"
        };
        svg.Append(Invariant, $"<text x=\"{N(text.X)}\" y=\"{N(text.Y)}\" font-size=\"{N(text.Size)}\" text-anchor=\"{anchor}\" dominant-baseline=\"central\"");
        if (text.Bold)
        {
            svg.Append(" font-weight=\"600\"");
        }

        svg.Append(Fill(text.Color)).Append('>').Append(SecurityElement.Escape(text.Text)).Append("</text>\n");
    }

    private static string Points(PlotPoint[] points)
    {
        StringBuilder builder = new(points.Length * 12);
        foreach (PlotPoint point in points)
        {
            builder.Append(N(point.X)).Append(',').Append(N(point.Y)).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }

    private static string Stroke(uint color, double thickness, LineDash dash)
    {
        string result = $" stroke=\"{Hex(color)}\" stroke-width=\"{N(thickness)}\"";
        double opacity = (color >> 24) / 255.0;
        if (opacity < 1)
        {
            result += $" stroke-opacity=\"{N(opacity)}\"";
        }

        string? pattern = DashPattern(dash, thickness);
        return pattern is null ? result : result + $" stroke-dasharray=\"{pattern}\"";
    }

    private static string Fill(uint color)
    {
        double opacity = (color >> 24) / 255.0;
        if (opacity <= 0)
        {
            return " fill=\"none\"";
        }

        string result = $" fill=\"{Hex(color)}\"";
        return opacity < 1 ? result + $" fill-opacity=\"{N(opacity)}\"" : result;
    }

    public static string? DashPattern(LineDash dash, double thickness) => dash switch
    {
        LineDash.Dash => $"{N(6 * Math.Max(1, thickness))} {N(3 * Math.Max(1, thickness))}",
        LineDash.Dot => $"{N(1.2 * Math.Max(1, thickness))} {N(2.4 * Math.Max(1, thickness))}",
        _ => null
    };

    private static string Hex(uint color) => $"#{(color >> 16) & 0xFF:X2}{(color >> 8) & 0xFF:X2}{color & 0xFF:X2}";

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", Invariant);
}
