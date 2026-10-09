using System.Globalization;

namespace AMK.LasViewer.Charting;

/// <summary>Страница планшета для печати или экспорта: заголовок, шапка треков и тело.</summary>
public sealed class TabletPage
{
    public required TabletScene Scene { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double Top { get; init; }

    public double Bottom { get; init; }
}

/// <summary>
/// Раскладывает планшет на страницы: треки сжимаются по ширине страницы,
/// глубина режется на участки по высоте тела страницы при заданном масштабе.
/// </summary>
public static class TabletPageBuilder
{
    private const double TitleLineHeight = 16;
    private const double TitlePadding = 6;

    public static double TitleHeight(IReadOnlyList<string> titleLines) =>
        titleLines.Count == 0 ? 0 : titleLines.Count * TitleLineHeight + TitlePadding * 2;

    /// <summary>Высота тела на странице: всё, что осталось под заголовком и шапкой.</summary>
    public static double BodyHeight(TabletTemplate template, IReadOnlyList<string> titleLines, double pageHeight, TabletSources? data = null) =>
        pageHeight - TitleHeight(titleLines) - TabletSceneBuilder.HeaderHeight(template, data);

    /// <summary>
    /// Участки глубины по страницам. Последний участок короче, если запись
    /// кончилась раньше страницы.
    /// </summary>
    public static IReadOnlyList<(double Top, double Bottom)> SplitDepth(double top, double bottom, double pixelsPerUnit, double bodyHeight)
    {
        List<(double, double)> pages = [];
        if (bodyHeight <= 1 || pixelsPerUnit <= 0 || bottom <= top)
        {
            return pages;
        }

        double span = bodyHeight / pixelsPerUnit;
        for (double pageTop = top; pageTop < bottom - span * 1e-6; pageTop += span)
        {
            pages.Add((pageTop, Math.Min(bottom, pageTop + span)));
        }

        return pages;
    }

    /// <summary>Ширины треков, сжатые или растянутые так, чтобы планшет занял ровно <paramref name="contentWidth"/>.</summary>
    public static double[] ScaleTrackWidths(TabletTemplate template, double contentWidth)
    {
        double total = template.Tracks.Sum(track => track.WidthDip);
        double factor = total > 0 ? Math.Max(0.1, (contentWidth - template.DepthTrackWidth) / total) : 1;
        return template.Tracks.Select(track => track.WidthDip * factor).ToArray();
    }

    public static TabletPage Build(
        TabletSources data,
        TabletTemplate template,
        DocumentAnnotations? annotations,
        IReadOnlyList<string> titleLines,
        IReadOnlyList<double> trackWidths,
        double top,
        double bottom,
        double pixelsPerUnit,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double width = template.DepthTrackWidth + trackWidths.Sum();
        double titleHeight = TitleHeight(titleLines);
        double headerHeight = TabletSceneBuilder.HeaderHeight(template, data);
        double bodyHeight = (bottom - top) * pixelsPerUnit;

        TabletScene page = new();
        if (titleLines.Count > 0)
        {
            SceneLayer title = new(0, 0, width, titleHeight);
            for (int index = 0; index < titleLines.Count; index++)
            {
                title.Items.Add(new SceneText(
                    2,
                    TitlePadding + TitleLineHeight * index + TitleLineHeight / 2,
                    titleLines[index],
                    index == 0 ? 12.5 : 10.5,
                    index == 0 ? SceneColors.Text : SceneColors.MutedText,
                    TextAnchor.Left,
                    index == 0));
            }

            page.Layers.Add(title);
        }

        AddShifted(page, TabletSceneBuilder.BuildHeader(data, template, trackWidths, culture), titleHeight);
        DepthViewport viewport = new(top, pixelsPerUnit, bodyHeight);
        AddShifted(page, TabletSceneBuilder.BuildBody(data, template, trackWidths, viewport, annotations, culture), titleHeight + headerHeight);

        // Рамка вокруг тела: на бумаге без неё обрез трека по нижнему краю
        // неотличим от конца записи.
        SceneLayer frame = new(0, titleHeight, width, headerHeight + bodyHeight);
        frame.Items.Add(new SceneRect(0.5, 0.5, width - 1, headerHeight + bodyHeight - 1, 0x00000000, SceneColors.Border));
        page.Layers.Add(frame);

        return new TabletPage
        {
            Scene = page,
            Width = width,
            Height = titleHeight + headerHeight + bodyHeight,
            Top = top,
            Bottom = bottom
        };
    }

    private static void AddShifted(TabletScene target, TabletScene source, double dy)
    {
        foreach (SceneLayer layer in source.Layers)
        {
            SceneLayer shifted = new(layer.X, layer.Y + dy, layer.Width, layer.Height);
            shifted.Items.AddRange(layer.Items);
            target.Layers.Add(shifted);
        }
    }
}
