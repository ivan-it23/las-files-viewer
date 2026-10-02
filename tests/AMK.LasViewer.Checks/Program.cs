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
