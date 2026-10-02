using System.Diagnostics;
using AMK.LasViewer.Charting;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;

// Проверки библиотеки планшета и чтения LAS через движок.
// Запуск: dotnet run --project tests/AMK.LasViewer.Checks
// Проверки на файлах скважины 3201 выполняются, если файлы есть в
// LASVIEWER_SAMPLES_DIR или в папке загрузок; иначе пропускаются.

int failures = 0;
int skipped = 0;

Check("линейная шкала: доли и обращённая шкала", () =>
{
    TrackScale scale = TrackScale.Linear(-10, 40);
    Near(scale.ToFraction(-10), 0);
    Near(scale.ToFraction(15), 0.5);
    Near(scale.ToFraction(90), 2);
    Near(TrackScale.Linear(100, 0).ToFraction(25), 0.75);
    Equal(scale.GridLines(200).Count, 11);
    Equal(scale.GridLines(200).Count(line => line.IsMajor), 3);
});

Check("логарифмическая шкала: декады и неположительные значения", () =>
{
    TrackScale scale = TrackScale.Logarithmic(1, 10000);
    Near(scale.ToFraction(100), 0.5);
    True(double.IsNaN(scale.ToFraction(0)), "ноль на лог-шкале должен давать NaN");
    IReadOnlyList<GridLine> wide = scale.GridLines(200);
    Equal(wide.Count(line => line.IsMajor), 5);
    Equal(wide.Count, 37);
    Equal(scale.GridLines(100).Count, 5);
});

Check("круглые границы линейной шкалы", () =>
{
    Equal(DefaultTemplateBuilder.NiceRange(-4.3, 41.2), (-5.0, 45.0));
    Equal(DefaultTemplateBuilder.NiceRange(0.2, 0.93), (0.2, 1.2));
    Equal(DefaultTemplateBuilder.NiceRange(5, 5), (4.5, 5.5));
});

Check("масштаб 1:500 — 2 мм экрана на метр", () =>
{
    double ppu = DepthViewport.PixelsPerUnitForRatio(500, "M");
    Near(ppu, 2 * DepthViewport.DipPerMillimetre);
    Near(DepthViewport.RatioForPixelsPerUnit(ppu, "M"), 500);
    Near(DepthViewport.PixelsPerUnitForRatio(500, "FT"), 0.6096 * DepthViewport.DipPerMillimetre);
});

Check("метки глубины: шаг подписей и кратность", () =>
{
    DepthViewport viewport = new(2006.04, DepthViewport.PixelsPerUnitForRatio(500, "M"), 700);
    Equal(DepthTicks.MajorStep(viewport), 10.0);
    IReadOnlyList<DepthTick> ticks = DepthTicks.Generate(viewport);
    True(ticks.All(tick => tick.Depth >= 2006.04 - 2 && tick.Depth <= viewport.Bottom + 2), "метки вне окна");
    True(ticks.Where(tick => tick.IsMajor).All(tick => Math.Abs(tick.Depth % 10) < 1e-6), "основные метки не кратны 10");
    True(ticks.Count(tick => !tick.IsMajor) > 0, "нет промежуточных меток");
});

Check("прореживание сохраняет выбросы и не превышает 4 точки на строку пикселей", () =>
{
    int count = 100_000;
    double[] depths = Enumerable.Range(0, count).Select(i => 1000 + i * 0.01).ToArray();
    double[] values = Enumerable.Range(0, count).Select(i => Math.Sin(i / 50.0)).ToArray();
    values[54_321] = 25;
    DepthViewport viewport = new(1000, 1, 1000);
    List<PlotPoint[]> segments = CurveDecimator.Build(depths, values, viewport, value => value * 10);
    int points = segments.Sum(segment => segment.Length);
    True(points <= 4 * 1002, $"точек {points}");
    True(segments.SelectMany(segment => segment).Any(point => point.X == 250), "выброс потерян");
    Equal(segments.Count, 1);
});

Check("пропуск разрывает ломаную", () =>
{
    double[] depths = [1, 2, 3, 4, 5];
    double[] values = [1, 2, double.NaN, 4, 5];
    List<PlotPoint[]> segments = CurveDecimator.Build(depths, values, new DepthViewport(0, 100, 600), value => value);
    Equal(segments.Count, 2);
    Equal(segments[0].Length, 2);
    Equal(segments[1].Length, 2);
});

Check("за окном берётся по одной точке, чтобы линия доходила до края", () =>
{
    double[] depths = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
    double[] values = depths.ToArray();
    List<PlotPoint[]> segments = CurveDecimator.Build(depths, values, new DepthViewport(10.5, 10, 100), value => value);
    Equal(segments[0][0].Y < 0, true);
    Equal(segments[0][^1].Y > 100, true);
});

Check("файл снизу вверх разворачивается, ближайшая строка ищется", () =>
{
    LasDocument document = Document(
        IndexDirection.Decreasing,
        [5, 4, double.NaN, 2, 1],
        [50, 40, 30, 20, 10]);
    TabletData data = TabletData.From(document);
    Equal(string.Join(",", data.Depths), "1,2,4,5");
    Equal(string.Join(",", data.Values("A")!), "10,20,40,50");
    Equal(data.NearestRow(2.9), 1);
    Equal(data.NearestRow(3.1), 2);
    Equal(data.NearestRow(-100), 0);
    Equal(data.NearestRow(100), 3);
});

Check("шаблон: УЭС на лог-шкале, по 4 кривые в треке", () =>
{
    LasDocument document = Document(
        IndexDirection.Increasing,
        [1, 2, 3],
        [1, 2, 3],
        ("F1", "grad"), ("F2", "grad"), ("F3", "grad"), ("F4", "grad"), ("F5", "grad"), ("RO1", "Омм"));
    TabletTemplate template = DefaultTemplateBuilder.Build(document);
    Equal(template.Tracks.Count, 3);
    Equal(template.Tracks[0].Curves.Count, 4);
    Equal(template.Tracks[1].Curves.Single().Mnemonic, "F5");
    Equal(template.Tracks[2].Scale.Kind, ScaleKind.Logarithmic);
});

Check("перенос за шкалу: обороты и прижатие после второго", () =>
{
    TrackScale scale = TrackScale.Linear(0, 10);
    Near(scale.ToWrappedFraction(5, out int band), 0.5);
    Equal(band, 0);
    Near(scale.ToWrappedFraction(10, out band), 1);
    Equal(band, 0);
    Near(scale.ToWrappedFraction(13, out band), 0.3);
    Equal(band, 1);
    Near(scale.ToWrappedFraction(-2, out band), 0.8);
    Equal(band, -1);
    Near(scale.ToWrappedFraction(95, out band), 1);
    Equal(band, 2);
    Near(TrackScale.Logarithmic(1, 100).ToWrappedFraction(1000, out band), 0.5);
    Equal(band, 1);
});

Check("заливка между кривыми меняет цвет в точке пересечения", () =>
{
    double[] depths = [0, 1, 2, 3, 4];
    double[] a = [1, 2, 3, 4, 5];
    double[] b = [3, 3, 3, 3, 3];
    List<FillPolygon> polygons = FillBuilder.Build(depths, a, value => value, row => b[row], new DepthViewport(0, 10, 40), 0xFF00FF00, 0xFFFF0000);
    Equal(polygons.Count, 2);
    Equal(polygons[0].Color, 0xFFFF0000u);
    Equal(polygons[1].Color, 0xFF00FF00u);
    // Оба многоугольника сходятся в точке пересечения: глубина 2 → y = 20, x = 3.
    True(polygons[0].Points.Contains(new PlotPoint(3, 20)), "нет точки пересечения в первом многоугольнике");
    True(polygons[1].Points.Contains(new PlotPoint(3, 20)), "нет точки пересечения во втором многоугольнике");
});

Check("заливка без цвета для одной стороны и разрыв на пропуске", () =>
{
    double[] depths = [0, 1, 2, 3, 4];
    double[] a = [5, 5, double.NaN, 5, 5];
    List<FillPolygon> polygons = FillBuilder.Build(depths, a, value => value, _ => 0, new DepthViewport(0, 10, 40), 0xFF00FF00, null);
    Equal(polygons.Count, 2);
    List<FillPolygon> negativeOnly = FillBuilder.Build(depths, [1, 1, 1, 1, 1], value => value, _ => 3, new DepthViewport(0, 10, 40), 0xFF00FF00, null);
    Equal(negativeOnly.Count, 0);
});

Check("шаблон и пометки: JSON туда и обратно без потерь", () =>
{
    TabletTemplate template = new() { Name = "Проверка", DepthTrackWidth = 80 };
    template.Tracks.Add(new TrackDefinition
    {
        Scale = TrackScale.Logarithmic(0.1, 1000),
        WidthDip = 210,
        Curves =
        [
            new CurveStyle { Mnemonic = "RO1", Color = 0xFF112233, Thickness = 2, Dash = LineDash.Dash, Wrap = WrapMode.Wrap },
            new CurveStyle { Mnemonic = "RO4", Scale = TrackScale.Linear(0, 50) }
        ],
        Fills = [new FillDefinition { Kind = FillKind.BetweenCurves, Curve = "RO1", OtherCurve = "RO4", Color = 0x5500FF00, OppositeColor = 0x55FF0000 }]
    });

    TabletTemplate copy = TemplateSerializer.ReadTemplate(TemplateSerializer.WriteTemplate(template));
    Equal(TemplateSerializer.WriteTemplate(copy), TemplateSerializer.WriteTemplate(template));
    Equal(copy.Tracks[0].Curves[0].Wrap, WrapMode.Wrap);
    Equal(copy.Tracks[0].Curves[1].Scale, TrackScale.Linear(0, 50));
    Equal(copy.Tracks[0].Fills[0].OppositeColor, 0x55FF0000u);

    DocumentAnnotations annotations = new();
    annotations.Markers.Add(new DepthMarker { Depth = 2150.5, Name = "Кровля", Color = 0xFFAA0000 });
    annotations.Intervals.Add(new DepthInterval { Top = 2150, Bottom = 2170, Name = "Брак записи" });
    DocumentAnnotations restored = TemplateSerializer.AnnotationsFromJson(TemplateSerializer.AnnotationsToJson(annotations));
    Equal(restored.Markers[0].Id, annotations.Markers[0].Id);
    Equal(restored.Markers[0].Name, "Кровля");
    Equal(restored.Intervals[0].Bottom, 2170.0);
    Equal(restored.MarkerNear(2150.6, 0.2)?.Name, "Кровля");
    Equal(restored.IntervalAt(2160)?.Name, "Брак записи");
});

Check("шаблон новее программы отвергается с понятным сообщением", () =>
{
    try
    {
        TemplateSerializer.ReadTemplate("{\"format\":\"lasviewer-template\",\"version\":99,\"template\":{}}");
        True(false, "должна быть ошибка формата");
    }
    catch (FormatException error)
    {
        True(error.Message.Contains("более новой программой"), error.Message);
    }
});

Check("разбивка по страницам и масштаб треков под ширину", () =>
{
    IReadOnlyList<(double Top, double Bottom)> pages = TabletPageBuilder.SplitDepth(2000, 2250, 10, 1000);
    Equal(pages.Count, 3);
    Equal(pages[0], (2000.0, 2100.0));
    Equal(pages[2], (2200.0, 2250.0));

    TabletTemplate template = new() { DepthTrackWidth = 70 };
    template.Tracks.Add(new TrackDefinition { Scale = TrackScale.Linear(0, 1), WidthDip = 100 });
    template.Tracks.Add(new TrackDefinition { Scale = TrackScale.Linear(0, 1), WidthDip = 300 });
    double[] widths = TabletPageBuilder.ScaleTrackWidths(template, 470);
    Near(widths[0], 100);
    Near(widths[1], 300);
    widths = TabletPageBuilder.ScaleTrackWidths(template, 270);
    Near(widths[0], 50);
});

await CheckAsync("скважина 3201: открытие через движок и планшет по умолчанию", async () =>
{
    string samples = Environment.GetEnvironmentVariable("LASVIEWER_SAMPLES_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "3201_32_Кийское_ИК прибор 004-2025", "DeviceBur1", "Индукционник", "RAM");
    string path = Path.Combine(samples, "3201 ИК.las");
    if (!File.Exists(path))
    {
        throw new SkipException($"нет файла {path}");
    }

    await using EngineHost engine = new(AppContext.BaseDirectory, "checks");
    True(await engine.StartAsync(CancellationToken.None), engine.LastError ?? "движок не запустился");

    Stopwatch watch = Stopwatch.StartNew();
    LasDocument document = await engine.OpenLasAsync(path, CancellationToken.None);
    Console.WriteLine($"    открыт за {watch.ElapsedMilliseconds} мс: {document.RowCount} строк, {document.Curves.Count} кривых, {document.EncodingDisplay}");

    Equal(document.RowCount, 10267);
    Equal(document.Curves.Count, 17);
    Equal(document.Index.Step, 0.1);
    Equal(document.Curves.Single(c => c.Mnemonic == "RO1_400_S").Values[0], 31.09759);
    True(double.IsNaN(document.Curves.Single(c => c.Mnemonic == "RO1_400_S").Values[^1]), "последний RO1_400_S должен быть пропуском");
    True(document.Issues.Any(issue => issue.Code == "nonstandard_section_name"), "нет замечания о ~Parametr");

    TabletTemplate template = DefaultTemplateBuilder.Build(document);
    foreach (TrackDefinition track in template.Tracks)
    {
        Console.WriteLine($"    трек: {string.Join(", ", track.Curves.Select(c => c.Mnemonic))} — {track.Scale.Kind} {track.Scale.Left:g}…{track.Scale.Right:g}");
    }

    Equal(template.Tracks.Count, 4);
    Equal(template.Tracks.Count(track => track.Scale.Kind == ScaleKind.Logarithmic), 2);

    TabletData data = TabletData.From(document);
    Equal(data.Top, 2006.04);
    Equal(data.Bottom, 3032.64);

    // Сцена со всеми видами элементов на реальных данных: перенос, заливка
    // между кривыми, маркер, интервал — и её SVG должен быть корректным XML.
    TrackDefinition resistivity = template.Tracks[2];
    resistivity.Curves[0].Wrap = WrapMode.Wrap;
    resistivity.Fills.Add(new FillDefinition
    {
        Kind = FillKind.BetweenCurves,
        Curve = "RO1_400_S",
        OtherCurve = "RO4_400_S",
        Color = 0x553A7BD5,
        OppositeColor = 0x55D62728
    });
    DocumentAnnotations annotations = new();
    annotations.Markers.Add(new DepthMarker { Depth = 2040, Name = "Маркер" });
    annotations.Intervals.Add(new DepthInterval { Top = 2150, Bottom = 2170, Name = "Проверить" });

    watch.Restart();
    double[] widths = TabletSceneBuilder.FitTrackWidths(template, 1000, 130);
    DepthViewport viewport = new(data.Top, DepthViewport.PixelsPerUnitForRatio(500, "M"), 800);
    TabletScene scene = TabletSceneBuilder.BuildBody(data, template, widths, viewport, annotations);
    Console.WriteLine($"    сцена 1:500 построена за {watch.ElapsedMilliseconds} мс: {scene.Layers.Sum(l => l.Items.Count)} элементов");
    True(scene.Layers[3].Items.OfType<ScenePolygon>().Any(), "нет заливки между RO1 и RO4");

    TabletPage page = TabletPageBuilder.Build(
        data, template, annotations, ["Скважина 3201", "3201 ИК.las"], widths, 2100, 2200, viewport.PixelsPerUnit);
    string svg = SvgWriter.Write(page.Scene, page.Width, page.Height, "3201");
    System.Xml.Linq.XDocument.Parse(svg);
    True(svg.Contains("Проверить") && svg.Contains("RO1_400_S") && !svg.Contains("Маркер"), "в SVG не те подписи: маркер 2040 м вне страницы 2100–2200 м");

    await engine.CloseLasAsync(document.FileId, CancellationToken.None);
    try
    {
        await engine.CloseLasAsync(document.FileId, CancellationToken.None);
        True(false, "повторное закрытие должно отказывать");
    }
    catch (EngineRpcException error) when (error.Code == LasEngineApi.UnknownFileId)
    {
    }

    try
    {
        await engine.OpenLasAsync(path + ".нет", CancellationToken.None);
        True(false, "открытие несуществующего файла должно отказывать");
    }
    catch (EngineRpcException error) when (error.Code == LasEngineApi.FileNotFound)
    {
    }
});

Console.WriteLine(failures == 0
    ? $"Все проверки пройдены{(skipped > 0 ? $", пропущено: {skipped}" : string.Empty)}."
    : $"Не пройдено проверок: {failures}.");
return failures == 0 ? 0 : 1;

void Check(string name, Action body)
{
    CheckAsync(name, () =>
    {
        body();
        return Task.CompletedTask;
    }).GetAwaiter().GetResult();
}

async Task CheckAsync(string name, Func<Task> body)
{
    Console.WriteLine($"• {name}");
    try
    {
        await body();
        Console.WriteLine("  OK");
    }
    catch (SkipException skip)
    {
        skipped++;
        Console.WriteLine($"  пропущено: {skip.Message}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"  ОШИБКА: {exception.GetType().Name}: {exception.Message}");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        throw new InvalidOperationException($"ожидалось {expected}, получено {actual}");
    }
}

static void Near(double actual, double expected)
{
    if (Math.Abs(actual - expected) > 1e-9 * Math.Max(1, Math.Abs(expected)))
    {
        throw new InvalidOperationException($"ожидалось {expected}, получено {actual}");
    }
}

static LasDocument Document(IndexDirection direction, double[] depths, double[] values, params (string Mnemonic, string Unit)[] curves)
{
    if (curves.Length == 0)
    {
        curves = [("A", "")];
    }

    List<LasCurve> list = [Curve("DEPT", "M", depths)];
    list.AddRange(curves.Select(curve => Curve(curve.Mnemonic, curve.Unit, values)));
    return new LasDocument
    {
        FileId = "test",
        Path = "test.las",
        Name = "test.las",
        EncodingDisplay = "ASCII",
        EncodingMethod = "ascii",
        Version = "2.0",
        Delimiter = "space",
        RowCount = depths.Length,
        Index = new LasIndexInfo("DEPT", "M", null, null, null, direction, null, null, null),
        Sections = [],
        Curves = list,
        Issues = []
    };

    static LasCurve Curve(string mnemonic, string unit, double[] values) => new()
    {
        Mnemonic = mnemonic,
        OriginalMnemonic = mnemonic,
        Unit = unit,
        ApiCode = string.Empty,
        Description = string.Empty,
        Statistics = new CurveStatistics(values.Length, 0, null, null, null),
        Values = values
    };
}

internal sealed class SkipException(string message) : Exception(message);
