using System.Globalization;

namespace AMK.LasViewer.Charting;

/// <summary>Строит сцену планшета: тело (треки с кривыми) и шапку (для экспорта и печати).</summary>
public static class TabletSceneBuilder
{
    public const double HeaderRowHeight = 30;
    public const double HeaderPadding = 6;

    private const int MaxWrapBands = 2;

    /// <summary>
    /// Ширины треков под доступную ширину: пропорционально ширинам шаблона;
    /// если какой-то трек стал бы уже <paramref name="minimumTrackWidth"/>,
    /// берутся ширины шаблона (тогда нужна горизонтальная прокрутка).
    /// </summary>
    public static double[] FitTrackWidths(TabletTemplate template, double availableWidth, double minimumTrackWidth)
    {
        double[] widths = template.Tracks.Select(track => track.WidthDip).ToArray();
        double total = widths.Sum();
        if (total <= 0 || availableWidth <= 0)
        {
            return widths;
        }

        double factor = (availableWidth - template.DepthTrackWidth - 1) / total;
        if (widths.Any(width => width * factor < minimumTrackWidth))
        {
            return widths;
        }

        return widths.Select(width => Math.Floor(width * factor)).ToArray();
    }

    // ------------------------------------------------------------------
    // Тело
    // ------------------------------------------------------------------

    public static TabletScene BuildBody(
        TabletSources data,
        TabletTemplate template,
        IReadOnlyList<double> trackWidths,
        DepthViewport viewport,
        DocumentAnnotations? annotations,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        TabletScene scene = new();
        IReadOnlyList<DepthTick> ticks = DepthTicks.Generate(viewport);
        double majorStep = DepthTicks.MajorStep(viewport);

        scene.Layers.Add(DepthLayer(template.DepthTrackWidth, viewport, ticks, majorStep, culture));

        double x = template.DepthTrackWidth;
        for (int index = 0; index < template.Tracks.Count; index++)
        {
            double width = index < trackWidths.Count ? trackWidths[index] : template.Tracks[index].WidthDip;
            SceneLayer layer = new(x, 0, width, viewport.HeightDip);
            DrawTrack(layer, template.Tracks[index], data, viewport, ticks, annotations);
            if (index == 0 && annotations is not null)
            {
                DrawAnnotationLabels(layer, viewport, annotations);
            }

            DrawRightBorder(layer);
            scene.Layers.Add(layer);
            x += width;
        }

        if (template.Tracks.Count == 0 && annotations is not null)
        {
            DrawAnnotationLabels(scene.Layers[0], viewport, annotations);
        }

        return scene;
    }

    private static SceneLayer DepthLayer(
        double width,
        DepthViewport viewport,
        IReadOnlyList<DepthTick> ticks,
        double majorStep,
        CultureInfo culture)
    {
        SceneLayer layer = new(0, 0, width, viewport.HeightDip);
        int decimals = majorStep >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(majorStep));
        List<(PlotPoint, PlotPoint)> segments = [];
        foreach (DepthTick tick in ticks)
        {
            double y = viewport.ToY(tick.Depth);
            double length = tick.IsMajor ? 12 : 6;
            segments.Add((new PlotPoint(width - length, y), new PlotPoint(width, y)));

            // Подпись у самого края обрезалась бы наполовину — она пропускается.
            if (tick.IsMajor && y >= 7 && y <= viewport.HeightDip - 7)
            {
                layer.Items.Add(new SceneText(
                    width - 16,
                    y,
                    tick.Depth.ToString("F" + decimals, culture),
                    11,
                    SceneColors.Text,
                    TextAnchor.Right));
            }
        }

        layer.Items.Insert(0, new SceneSegments(segments, SceneColors.MutedText, 1));
        DrawRightBorder(layer);
        return layer;
    }

    private static void DrawTrack(
        SceneLayer layer,
        TrackDefinition track,
        TabletSources data,
        DepthViewport viewport,
        IReadOnlyList<DepthTick> ticks,
        DocumentAnnotations? annotations)
    {
        double width = layer.Width;

        List<(PlotPoint, PlotPoint)> minor = [];
        List<(PlotPoint, PlotPoint)> major = [];
        foreach (DepthTick tick in ticks)
        {
            double y = viewport.ToY(tick.Depth);
            (tick.IsMajor ? major : minor).Add((new PlotPoint(0, y), new PlotPoint(width, y)));
        }

        foreach (GridLine line in track.Scale.GridLines(width))
        {
            double x = Math.Round(line.Fraction * width) + 0.5;
            (line.IsMajor ? major : minor).Add((new PlotPoint(x, 0), new PlotPoint(x, viewport.HeightDip)));
        }

        layer.Items.Add(new SceneSegments(minor, SceneColors.MinorGrid, 1));
        layer.Items.Add(new SceneSegments(major, SceneColors.MajorGrid, 1));

        if (annotations is not null)
        {
            foreach (DepthInterval interval in annotations.Intervals)
            {
                double y1 = viewport.ToY(Math.Min(interval.Top, interval.Bottom));
                double y2 = viewport.ToY(Math.Max(interval.Top, interval.Bottom));
                if (y2 < 0 || y1 > viewport.HeightDip)
                {
                    continue;
                }

                layer.Items.Add(new SceneRect(0, y1, width, y2 - y1, SceneColors.WithAlpha(interval.Color, 0x26)));
                uint edge = SceneColors.WithAlpha(interval.Color, 0xB0);
                layer.Items.Add(new SceneSegments(
                    [(new PlotPoint(0, y1), new PlotPoint(width, y1)), (new PlotPoint(0, y2), new PlotPoint(width, y2))],
                    edge,
                    1,
                    LineDash.Dash));
            }
        }

        foreach (FillDefinition fill in track.Fills)
        {
            DrawFill(layer, track, fill, data, viewport);
        }

        foreach (CurveStyle style in track.Curves)
        {
            DrawCurve(layer, track, style, data, viewport);
        }

        if (annotations is not null)
        {
            foreach (DepthMarker marker in annotations.Markers)
            {
                double y = viewport.ToY(marker.Depth);
                if (y < -2 || y > viewport.HeightDip + 2)
                {
                    continue;
                }

                layer.Items.Add(new SceneSegments([(new PlotPoint(0, y), new PlotPoint(width, y))], marker.Color, 1.6));
            }
        }
    }

    private static void DrawCurve(SceneLayer layer, TrackDefinition track, CurveStyle style, TabletSources data, DepthViewport viewport)
    {
        // Каждая кривая — по своим глубинам: кривая другого файла не
        // пересчитывается на сетку планшета.
        if (data.Series(style) is not { } series)
        {
            return;
        }

        double[] depths = series.Depths;
        double[] values = series.Values;

        TrackScale scale = style.EffectiveScale(track);
        double width = layer.Width;

        if (style.Wrap == WrapMode.Clamp)
        {
            // Значение за пределами шкалы прижимается к краю трека, а не
            // исчезает: пользователь видит, что кривая ушла за шкалу.
            double ToX(double value)
            {
                double fraction = scale.ToFraction(value);
                return double.IsNaN(fraction) ? double.NaN : Math.Clamp(fraction, 0, 1) * width;
            }

            AddPolylines(layer, CurveDecimator.Build(depths, values, viewport, ToX), style.Color, style.Thickness, style.Dash);
            return;
        }

        // Каждый оборот шкалы — отдельная серия: значения других оборотов в
        // ней пропуски, поэтому на переходе ломаная рвётся, а не пересекает трек.
        for (int band = -MaxWrapBands; band <= MaxWrapBands; band++)
        {
            int current = band;
            double ToX(double value)
            {
                double fraction = scale.ToWrappedFraction(value, out int valueBand, MaxWrapBands);
                return double.IsNaN(fraction) || valueBand != current ? double.NaN : fraction * width;
            }

            List<PlotPoint[]> segments = CurveDecimator.Build(depths, values, viewport, ToX);
            if (band == 0)
            {
                AddPolylines(layer, segments, style.Color, style.Thickness, style.Dash);
            }
            else
            {
                LineDash dash = style.Dash == LineDash.Solid ? LineDash.Dash : LineDash.Dot;
                AddPolylines(layer, segments, style.Color, Math.Max(0.6, style.Thickness * 0.8), dash);
            }
        }
    }

    private static void DrawFill(SceneLayer layer, TrackDefinition track, FillDefinition fill, TabletSources data, DepthViewport viewport)
    {
        // Обе кривые заливки — из одного файла (fill.Source), на одной сетке глубин.
        CurveStyle? style = track.Curves.FirstOrDefault(curve => curve.Mnemonic == fill.Curve && curve.Source == fill.Source);
        if (data.Series(fill.Source, fill.Curve) is not { } series)
        {
            return;
        }

        double[] values = series.Values;

        double width = layer.Width;
        TrackScale scale = style?.EffectiveScale(track) ?? track.Scale;
        Func<double, double> toX = ClampedX(scale, width);

        Func<int, double> boundary;
        uint? negative = fill.OppositeColor;
        switch (fill.Kind)
        {
            case FillKind.ToLeftEdge:
                boundary = _ => 0;
                negative = fill.Color;
                break;
            case FillKind.ToRightEdge:
                boundary = _ => width;
                negative = fill.Color;
                break;
            case FillKind.ToValue:
            {
                double x = toX(fill.Value);
                boundary = _ => x;
                break;
            }

            default:
            {
                double[]? other = fill.OtherCurve is null ? null : data.Series(fill.Source, fill.OtherCurve)?.Values;
                if (other is null)
                {
                    return;
                }

                CurveStyle? otherStyle = track.Curves.FirstOrDefault(curve => curve.Mnemonic == fill.OtherCurve && curve.Source == fill.Source);
                Func<double, double> otherX = ClampedX(otherStyle?.EffectiveScale(track) ?? track.Scale, width);
                boundary = row => otherX(other[row]);
                break;
            }
        }

        foreach (FillPolygon polygon in FillBuilder.Build(series.Depths, values, toX, boundary, viewport, fill.Color, negative))
        {
            layer.Items.Add(new ScenePolygon(polygon.Points, polygon.Color));
        }
    }

    private static Func<double, double> ClampedX(TrackScale scale, double width)
    {
        return value =>
        {
            double fraction = scale.ToFraction(value);
            return double.IsNaN(fraction) ? double.NaN : Math.Clamp(fraction, 0, 1) * width;
        };
    }

    private static void AddPolylines(SceneLayer layer, List<PlotPoint[]> segments, uint color, double thickness, LineDash dash)
    {
        foreach (PlotPoint[] segment in segments)
        {
            if (segment.Length >= 2)
            {
                layer.Items.Add(new ScenePolyline(segment, color, thickness, dash));
            }
        }
    }

    private static void DrawAnnotationLabels(SceneLayer layer, DepthViewport viewport, DocumentAnnotations annotations)
    {
        foreach (DepthInterval interval in annotations.Intervals)
        {
            double top = viewport.ToY(Math.Min(interval.Top, interval.Bottom));
            double bottom = viewport.ToY(Math.Max(interval.Top, interval.Bottom));
            if (bottom < 0 || top > viewport.HeightDip || interval.Name.Length == 0)
            {
                continue;
            }

            // Подпись держится у верхнего края видимой части интервала.
            double y = Math.Max(top, 0) + 9;
            if (y < bottom)
            {
                layer.Items.Add(new SceneText(4, y, interval.Name, 10.5, interval.Color | 0xFF000000, TextAnchor.Left, true, SceneColors.LabelBackground));
            }
        }

        foreach (DepthMarker marker in annotations.Markers)
        {
            double y = viewport.ToY(marker.Depth);
            if (y < 0 || y > viewport.HeightDip || marker.Name.Length == 0)
            {
                continue;
            }

            layer.Items.Add(new SceneText(4, y - 8, marker.Name, 10.5, marker.Color | 0xFF000000, TextAnchor.Left, true, SceneColors.LabelBackground));
        }
    }

    private static void DrawRightBorder(SceneLayer layer)
    {
        double x = layer.Width - 0.5;
        layer.Items.Add(new SceneSegments([(new PlotPoint(x, 0), new PlotPoint(x, layer.Height))], SceneColors.Border, 1));
    }

    // ------------------------------------------------------------------
    // Шапка (для экспорта и печати; на экране шапка интерактивная)
    // ------------------------------------------------------------------

    /// <param name="data">Файлы планшета: у совместного над треком одного файла — строка с именем файла.</param>
    public static double HeaderHeight(TabletTemplate template, TabletSources? data = null)
    {
        int rows = Math.Max(1, template.Tracks.Count == 0
            ? 1
            : template.Tracks.Max(track => track.Curves.Count + (data is not null && TrackCaption(data, track) is not null ? 1 : 0)));
        return rows * HeaderRowHeight + HeaderPadding * 2;
    }

    public static TabletScene BuildHeader(
        TabletSources data,
        TabletTemplate template,
        IReadOnlyList<double> trackWidths,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        TabletScene scene = new();
        double height = HeaderHeight(template, data);

        SceneLayer depth = new(0, 0, template.DepthTrackWidth, height);
        depth.Items.Add(new SceneText(template.DepthTrackWidth / 2, height - 26, "Глубина", 11.5, SceneColors.Text, TextAnchor.Center, true));
        depth.Items.Add(new SceneText(template.DepthTrackWidth / 2, height - 12, data.IndexUnit.Length > 0 ? data.IndexUnit : "—", 10.5, SceneColors.MutedText, TextAnchor.Center));
        DrawHeaderFrame(depth);
        scene.Layers.Add(depth);

        double x = template.DepthTrackWidth;
        for (int index = 0; index < template.Tracks.Count; index++)
        {
            TrackDefinition track = template.Tracks[index];
            double width = index < trackWidths.Count ? trackWidths[index] : track.WidthDip;
            SceneLayer layer = new(x, 0, width, height);

            // Строки кривых прижаты к низу шапки, как на экране.
            double rowTop = height - HeaderPadding - track.Curves.Count * HeaderRowHeight;
            if (TrackCaption(data, track) is { } caption)
            {
                layer.Items.Add(new SceneText(width / 2, rowTop - HeaderRowHeight / 2, caption, 10.5, SceneColors.MutedText, TextAnchor.Center, true));
            }

            foreach (CurveStyle style in track.Curves)
            {
                TrackScale scale = style.EffectiveScale(track);
                double nameY = rowTop + 9;
                double scaleY = rowTop + 22;
                layer.Items.Add(new SceneSegments([(new PlotPoint(6, nameY), new PlotPoint(22, nameY))], style.Color, 2.5, style.Dash));
                layer.Items.Add(new SceneText(26, nameY, CurveTitle(data, style, track), 11, style.Color, TextAnchor.Left, true));

                TabletSeries? series = data.Series(style);
                bool present = series is not null;
                string unit = series?.Unit ?? data.MissingReason(style.Source);
                if (scale.Kind == ScaleKind.Logarithmic && present)
                {
                    unit = unit.Length > 0 ? unit + ", лог." : "лог.";
                }

                layer.Items.Add(new SceneText(6, scaleY, FormatScale(scale.Left, culture), 9.5, SceneColors.MutedText));
                layer.Items.Add(new SceneText(width / 2, scaleY, unit, 9.5, SceneColors.MutedText, TextAnchor.Center));
                layer.Items.Add(new SceneText(width - 6, scaleY, FormatScale(scale.Right, culture), 9.5, SceneColors.MutedText, TextAnchor.Right));
                layer.Items.Add(new SceneSegments([(new PlotPoint(6, rowTop + HeaderRowHeight - 1), new PlotPoint(width - 6, rowTop + HeaderRowHeight - 1))], style.Color, 1));
                rowTop += HeaderRowHeight;
            }

            DrawHeaderFrame(layer);
            scene.Layers.Add(layer);
            x += width;
        }

        return scene;
    }

    /// <summary>
    /// Подпись кривой: мнемоника и имя её файла, если кривая не из файла
    /// планшета. Если имя файла уже стоит над треком (<see cref="TrackCaption"/>),
    /// у кривой его нет.
    /// </summary>
    public static string CurveTitle(TabletSources data, CurveStyle style, TrackDefinition track) =>
        data.Label(style.Source) is { } file && TrackCaption(data, track) is null ? $"{style.Mnemonic} · {file}" : style.Mnemonic;

    /// <summary>
    /// Имя файла над треком совместного планшета, если все кривые трека из
    /// одного файла; <c>null</c> — у планшета файла и у смешанного трека.
    /// </summary>
    public static string? TrackCaption(TabletSources data, TrackDefinition track)
    {
        if (!data.IsCombined || track.Curves.Count == 0)
        {
            return null;
        }

        string? source = track.Curves[0].Source;
        return track.Curves.All(curve => curve.Source == source) ? data.Find(source)?.Name : null;
    }

    /// <summary>Граница шкалы без экспоненты: «10000», а не «1E+04».</summary>
    public static string FormatScale(double value, CultureInfo culture) => value.ToString("0.####", culture);

    private static void DrawHeaderFrame(SceneLayer layer)
    {
        double right = layer.Width - 0.5;
        double bottom = layer.Height - 0.5;
        layer.Items.Add(new SceneSegments(
            [(new PlotPoint(right, 0), new PlotPoint(right, layer.Height)), (new PlotPoint(0, bottom), new PlotPoint(layer.Width, bottom))],
            SceneColors.Border,
            1));
    }
}
