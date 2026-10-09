using System.Diagnostics;
using AMK.LasViewer.Agent;
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

Check("проект: запись, чтение и поиск файлов после переноса папки", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "lasviewer-checks-" + Guid.NewGuid().ToString("N"));
    string data = Path.Combine(root, "Скважина 3201", "данные");
    Directory.CreateDirectory(data);
    string lasPath = Path.Combine(data, "400.las");
    File.WriteAllText(lasPath, "~V");
    string projectPath = Path.Combine(root, "Скважина 3201", "проект.lvproj");

    TabletTemplate template = new() { Name = "ИК" };
    template.Tracks.Add(new TrackDefinition { Scale = TrackScale.Logarithmic(1, 10000), Curves = [new CurveStyle { Mnemonic = "RO1_400_S" }] });
    DocumentAnnotations annotations = new();
    annotations.Markers.Add(new DepthMarker { Depth = 2032.2, Name = "Кровля" });
    ProjectSnapshot snapshot = new() { SyncDepth = false, Panels = [0] };
    snapshot.Documents.Add(new ProjectDocumentEntry { Path = lasPath, Template = template, Annotations = annotations, ViewTop = 2100, ViewPixelsPerUnit = 7.5 });

    string json = ProjectSerializer.Write(snapshot, projectPath);
    ProjectSnapshot read = ProjectSerializer.Read(json);
    Equal(read.Documents[0].RelativePath, Path.Combine("данные", "400.las"));
    Equal(read.Documents[0].Template.Name, "ИК");
    Equal(read.Documents[0].Annotations.Markers[0].Name, "Кровля");
    Equal(read.Documents[0].ViewTop, 2100.0);
    Equal(read.SyncDepth, false);
    Equal(string.Join(",", read.Panels), "0");

    // Папку проекта вместе с данными перенесли: полный путь устарел,
    // файл находится по относительному.
    string moved = root + "-перенос";
    Directory.Move(root, moved);
    string movedProject = Path.Combine(moved, "Скважина 3201", "проект.lvproj");
    Equal(ProjectSerializer.Resolve(read.Documents[0], movedProject), Path.Combine(moved, "Скважина 3201", "данные", "400.las"));
    File.Delete(Path.Combine(moved, "Скважина 3201", "данные", "400.las"));
    Equal(ProjectSerializer.Resolve(read.Documents[0], movedProject), null);
    Directory.Delete(moved, recursive: true);
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

    // По просьбе (LASVIEWER_DEMO_PROJECT) — проект с 400.las и 2000.las рядом
    // для ручной и автоматической проверки окна.
    if (Environment.GetEnvironmentVariable("LASVIEWER_DEMO_PROJECT") is { Length: > 0 } demoPath)
    {
        ProjectSnapshot demo = new() { SyncDepth = true, Panels = [0, 1] };
        foreach (string name in new[] { "400.las", "2000.las" })
        {
            LasDocument part = await engine.OpenLasAsync(Path.Combine(samples, name), CancellationToken.None);
            TabletTemplate partTemplate = DefaultTemplateBuilder.Build(part);
            partTemplate.Tracks[^1].Fills.Add(new FillDefinition
            {
                Kind = FillKind.BetweenCurves,
                Curve = partTemplate.Tracks[^1].Curves[0].Mnemonic,
                OtherCurve = partTemplate.Tracks[^1].Curves[^1].Mnemonic,
                Color = 0x553A7BD5,
                OppositeColor = 0x55D62728
            });
            DocumentAnnotations notes = new();
            notes.Intervals.Add(new DepthInterval { Top = 2150, Bottom = 2170, Name = "Проверить запись" });
            notes.Markers.Add(new DepthMarker { Depth = 2032.2, Name = "Маркер" });
            demo.Documents.Add(new ProjectDocumentEntry { Path = Path.Combine(samples, name), Template = partTemplate, Annotations = notes });
        }

        File.WriteAllText(demoPath, ProjectSerializer.Write(demo, demoPath));
        Console.WriteLine($"    проект для проверки окна: {demoPath}");
    }

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

// ----------------------------------------------------------------------
// ИИ-агент: поток посредника, цикл с инструментами, настройки
// ----------------------------------------------------------------------

await CheckAsync("агент: разбор потока — текст, вызов инструмента по частям, расход", async () =>
{
    string sse = string.Join("\n",
        "data: {\"choices\":[{\"delta\":{\"content\":\"Смотрю \"}}]}",
        "",
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"curve_stats\",\"arguments\":\"{\\\"file_id\\\":\"}}]}}]}",
        "",
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"f1\\\",\\\"curves\\\":[\\\"A\\\"]}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
        "",
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30,\"cost_rub\":0.0421}}",
        "",
        "data: [DONE]",
        "",
        "data: {\"choices\":[{\"delta\":{\"content\":\"после конца — не читается\"}}]}");
    List<string> pieces = [];
    ChatCompletion completion = await LlmProxyClient.ReadStreamAsync(new StringReader(sse), pieces.Add, CancellationToken.None);
    Equal(completion.Content, "Смотрю ");
    Equal(pieces.Count, 1);
    Equal(completion.ToolCalls.Count, 1);
    Equal(completion.ToolCalls[0].Name, "curve_stats");
    Equal(completion.ToolCalls[0].Arguments, "{\"file_id\":\"f1\",\"curves\":[\"A\"]}");
    Equal(completion.FinishReason, "tool_calls");
    Equal(completion.Usage, new ChatUsage(120, 30, 0.0421m));
});

await CheckAsync("агент: ошибка посредника в потоке и коды ответа сайта", async () =>
{
    try
    {
        await LlmProxyClient.ReadStreamAsync(
            new StringReader("event: proxy_error\ndata: {\"code\":\"upstream_error\",\"status\":402,\"message\":\"На счёте polza.ai недостаточно средств.\"}\n\n"),
            _ => { }, CancellationToken.None);
        True(false, "ошибка посредника не распознана");
    }
    catch (LlmProxyException error)
    {
        Equal(error.Code, "upstream_error");
        True(error.Message.Contains("недостаточно средств"), error.Message);
    }

    using FakeProxy unauthorized = new((_, _) => FakeProxy.Json(System.Net.HttpStatusCode.Unauthorized, "{\"success\":false,\"code\":\"unauthorized\",\"message\":\"Токен недействителен или отозван.\"}"));
    using LlmProxyClient client = new("amk-horizon.ru", "amkdv_test", unauthorized);
    try
    {
        await client.GetStatusAsync(CancellationToken.None);
        True(false, "401 не превратился в ошибку");
    }
    catch (LlmProxyException error)
    {
        True(error.IsAuthorizationProblem, error.Code);
    }

    using FakeProxy missing = new((_, _) => FakeProxy.Json(System.Net.HttpStatusCode.NotFound, "<html>Controller 'Llm' not found.</html>"));
    using LlmProxyClient old = new("https://amk-horizon.ru/front.php", "amkdv_test", missing);
    try
    {
        await old.GetModelsAsync(CancellationToken.None);
        True(false, "404 не превратился в ошибку");
    }
    catch (LlmProxyException error)
    {
        Equal(error.Code, "not_deployed");
    }

    Equal(LlmProxyClient.BuildEndpoint("amk-horizon.ru/"), "https://amk-horizon.ru/front.php");
    Equal(LlmProxyClient.BuildEndpoint("http://127.0.0.1:8765"), "http://127.0.0.1:8765/front.php");
    Equal(LlmProxyClient.BuildEndpoint("https://amk-horizon.ru/las/LlmController.php"), "https://amk-horizon.ru/las/LlmController.php");
    try
    {
        LlmProxyClient.BuildEndpoint("http://amk-horizon.ru");
        True(false, "адрес без https принят");
    }
    catch (ArgumentException)
    {
    }
});

await CheckAsync("агент: цикл — вызов инструмента, результат модели, ответ; токен в заголовке", async () =>
{
    List<System.Text.Json.Nodes.JsonObject> bodies = [];
    int call = 0;
    using FakeProxy proxy = new((request, body) =>
    {
        True(request.Headers.Authorization?.Parameter == "amkdv_secret", "нет токена в Authorization");
        True(request.RequestUri!.Query == "?c=Llm&a=chat", request.RequestUri.Query);
        bodies.Add((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(body)!);
        return ++call == 1
            ? FakeProxy.Sse(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"curve_stats\",\"arguments\":\"{\\\"file_id\\\":\\\"f1\\\",\\\"curves\\\":[\\\"RO4\\\"]}\"}}]}}]}",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"cost_rub\":0.5}}",
                "data: [DONE]")
            : FakeProxy.Sse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Среднее RO4 — 12 Ом·м.\"}}]}",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":8,\"cost_rub\":0.25}}",
                "data: [DONE]");
    });
    using LlmProxyClient client = new("amk-horizon.ru", "amkdv_secret", proxy);
    FakeTools tools = new();
    RecordingObserver observer = new();
    AgentSession session = new();

    string answer = await session.AskAsync("Какое среднее RO4?", "Открыт f1", client, null, tools, observer, CancellationToken.None);

    Equal(answer, "Среднее RO4 — 12 Ом·м.");
    Same(tools.Calls, ["curve_stats {\"file_id\":\"f1\",\"curves\":[\"RO4\"]}"]);
    Same(session.History.Select(m => m.Role), ["user", "assistant", "tool", "assistant"]);
    Equal(session.SpentRub, 0.75m);
    Same(observer.Events, ["tool+ curve_stats", "tool✓ curve_stats", "text Среднее RO4 — 12 Ом·м."]);

    System.Text.Json.Nodes.JsonArray second = bodies[1]["messages"]!.AsArray();
    Equal(second[0]!["role"]!.GetValue<string>(), "system");
    True(second[0]!["content"]!.GetValue<string>().Contains("Открыт f1"), "контекст не попал в системное сообщение");
    Equal(second[2]!["tool_calls"]![0]!["id"]!.GetValue<string>(), "c1");
    Equal(second[3]!["role"]!.GetValue<string>(), "tool");
    Equal(second[3]!["tool_call_id"]!.GetValue<string>(), "c1");
    Equal(bodies[0]["tool_choice"]!.GetValue<string>(), "auto");
    True(bodies[0]["tools"]!.AsArray().Count == 1, "инструменты не переданы");
});

await CheckAsync("агент: без настроек — общий доступ по ключу программы, токен необязателен", async () =>
{
    True(new AgentSettings().IsConfigured, "настройки по умолчанию должны работать без токена");
    Equal(LlmProxyClient.BuildEndpoint(new AgentSettings().ServerUrl), "https://amk-horizon.ru/las-viewer/LlmController.php");

    HttpRequestMessage? seen = null;
    using FakeProxy proxy = new((request, _) =>
    {
        seen = request;
        return FakeProxy.Json(System.Net.HttpStatusCode.OK,
            "{\"success\":true,\"shared\":true,\"user\":{\"username\":\"las-viewer\",\"full_name\":\"Общий доступ\"},\"default_model\":\"deepseek/deepseek-v3.2-exp\","
            + "\"limits\":{\"requests_per_hour\":300,\"requests_last_hour\":0,\"daily_budget_rub\":1000,\"spent_today_rub\":0},\"configured\":true}");
    });
    using LlmProxyClient shared = new(AgentSettings.DefaultServerUrl, null, proxy);
    ProxyStatus status = await shared.GetStatusAsync(CancellationToken.None);
    True(status.Shared, "общий доступ не распознан");
    Equal(status.DefaultModel, "deepseek/deepseek-v3.2-exp");
    True(seen!.Headers.GetValues("X-Client-Key").Single() == AgentSettings.BuiltInClientKey, "нет ключа программы");
    True(seen.Headers.Authorization is null && !seen.Headers.Contains("X-Api-Token"), "без токена не должно быть заголовков токена");

    using LlmProxyClient personal = new(AgentSettings.DefaultServerUrl, "amkdv_x", proxy);
    await personal.GetStatusAsync(CancellationToken.None);
    True(seen.Headers.Authorization?.Parameter == "amkdv_x" && seen.Headers.Contains("X-Client-Key"), "токен и ключ программы не переданы вместе");
});

await CheckAsync("агент: оборванный поток — продолжение с места обрыва, повтор, ошибка после повторных обрывов", async () =>
{
    // Поток без [DONE] и без причины завершения — оборван; вызовы инструментов из него отбрасываются.
    ChatCompletion broken = await LlmProxyClient.ReadStreamAsync(new StringReader(
        "data: {\"choices\":[{\"delta\":{\"content\":\"Начало \"}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"curve_stats\",\"arguments\":\"{\\\"fi\"}}]}}]}\n\n"),
        _ => { }, CancellationToken.None);
    True(broken.Interrupted && broken.ToolCalls.Count == 0 && broken.Content == "Начало ", "оборванный поток не распознан");

    // Продолжение: показанный текст остаётся, модель дописывает, в истории — одно сообщение.
    int calls = 0;
    using FakeProxy continuing = new((_, body) =>
    {
        calls++;
        if (calls == 1)
        {
            return FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Начало \"}}]}");
        }

        System.Text.Json.Nodes.JsonArray messages = System.Text.Json.Nodes.JsonNode.Parse(body)!["messages"]!.AsArray();
        Equal(messages[^2]!["role"]!.GetValue<string>(), "assistant");
        Equal(messages[^2]!["content"]!.GetValue<string>(), "Начало ");
        True(messages[^1]!["content"]!.GetValue<string>().Contains("оборвался"), "нет просьбы продолжить");
        return FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"конец.\"},\"finish_reason\":\"stop\"}]}", "data: [DONE]");
    });
    using LlmProxyClient client = new("amk-horizon.ru", null, continuing);
    AgentSession session = new();
    RecordingObserver observer = new();
    string answer = await session.AskAsync("?", string.Empty, client, null, new FakeTools(), observer, CancellationToken.None);
    Equal(answer, "Начало конец.");
    Same(session.History.Select(m => m.Role), ["user", "assistant"]);
    Same(observer.Events, ["text Начало ", "text конец."]);

    // Обрыв до первого слова — шаг повторяется с исходным запросом.
    int retries = 0;
    using FakeProxy empty = new((_, _) => ++retries == 1
        ? FakeProxy.Sse(": ping")
        : FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Готово\"},\"finish_reason\":\"stop\"}]}", "data: [DONE]"));
    using LlmProxyClient retryClient = new("amk-horizon.ru", null, empty);
    RecordingObserver retryObserver = new();
    Equal(await new AgentSession().AskAsync("?", string.Empty, retryClient, null, new FakeTools(), retryObserver, CancellationToken.None), "Готово");
    True(retryObserver.Events[0].StartsWith("notice ", StringComparison.Ordinal), "нет сообщения о повторе");

    // Обрывы подряд — понятная ошибка, история чистая.
    using FakeProxy always = new((_, _) => FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"…\"}}]}"));
    using LlmProxyClient failing = new("amk-horizon.ru", null, always);
    AgentSession failed = new();
    try
    {
        await failed.AskAsync("?", string.Empty, failing, null, new FakeTools(), new RecordingObserver(), CancellationToken.None);
        True(false, "повторные обрывы не привели к ошибке");
    }
    catch (LlmProxyException error)
    {
        Equal(error.Code, "stream_broken");
    }

    Equal(failed.History.Count, 0);
});

await CheckAsync("агент: прерванный вопрос не оставляет в истории вызов без результата", async () =>
{
    using CancellationTokenSource cancel = new();
    using FakeProxy proxy = new((_, _) => FakeProxy.Sse(
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"curve_stats\",\"arguments\":\"{}\"}}]}}]}",
        "data: [DONE]"));
    using LlmProxyClient client = new("amk-horizon.ru", "t", proxy);
    AgentSession session = new();
    FakeTools tools = new() { OnCall = cancel.Cancel };
    try
    {
        await session.AskAsync("вопрос", string.Empty, client, null, tools, new RecordingObserver(), cancel.Token);
        True(false, "отмена не сработала");
    }
    catch (OperationCanceledException)
    {
    }

    Equal(session.History.Count, 0);
});

await CheckAsync("агент: лимит шагов — после него инструменты отключаются", async () =>
{
    List<string> choices = [];
    using FakeProxy proxy = new((_, body) =>
    {
        System.Text.Json.Nodes.JsonNode request = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        // Без инструментов в запросе — последний шаг: tools и tool_choice не передаются.
        string choice = request["tool_choice"]?.GetValue<string>() ?? "none";
        if (choice == "none")
        {
            True(request["tools"] is null, "на последнем шаге инструменты не должны передаваться");
            string last = request["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            True(last.Contains("Лимит обращений"), "нет просьбы ответить по собранным данным");
        }
        choices.Add(choice);
        return choice == "auto"
            ? FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"list_files\",\"arguments\":\"{}\"}}]}}]}", "data: [DONE]")
            : FakeProxy.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Итог\"}}]}", "data: [DONE]");
    });
    using LlmProxyClient client = new("amk-horizon.ru", "t", proxy);
    AgentSession session = new();
    string answer = await session.AskAsync("?", string.Empty, client, null, new FakeTools(), new RecordingObserver(), CancellationToken.None);
    Equal(answer, "Итог");
    Equal(choices.Count, AgentSession.MaxToolRounds + 1);
    Equal(choices[^1], "none");
});

Check("агент: токен на диске только зашифрованным", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "lasviewer-checks-" + Guid.NewGuid().ToString("N"));
    try
    {
        AgentSettingsStore store = new(Path.Combine(directory, "agent-settings.json"));
        AgentSettings settings = new() { ServerUrl = "https://amk-horizon.ru", Token = "amkdv_0123456789abcdef", Model = "m" };
        True(store.TrySave(settings), "настройки не сохранены");
        string text = File.ReadAllText(store.SettingsPath);
        True(!text.Contains("0123456789abcdef"), "токен записан открытым текстом");
        AgentSettings loaded = store.Load();
        Equal(loaded.Token, "amkdv_0123456789abcdef");
        Equal(loaded.Model, "m");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
});

await CheckAsync("агент: инструменты движка через JSON-RPC", async () =>
{
    string path = Path.Combine(Path.GetTempPath(), "lasviewer-agent-check-" + Guid.NewGuid().ToString("N") + ".las");
    File.WriteAllText(path, string.Join("\n",
        "~V", "VERS. 2.0 :", "WRAP. NO :", "~W", "NULL. -999.25 :", "WELL. 51 :", "~C", "DEPT.M :", "RO.OHMM : сопротивление", "~A",
        "100.0 5", "100.1 7000", "100.2 7000", "100.3 7000", "100.4 6"));
    try
    {
        await using EngineHost engine = new(AppContext.BaseDirectory, "checks");
        True(await engine.StartAsync(CancellationToken.None), engine.LastError ?? "движок не запустился");
        LasDocument document = await engine.OpenLasAsync(path, CancellationToken.None);
        EngineAgentTools tools = new(engine);

        System.Text.Json.Nodes.JsonArray list = await tools.GetToolsAsync(CancellationToken.None);
        True(list.Count >= 5, $"инструментов {list.Count}");

        ToolResult found = await tools.CallAsync(
            "find_intervals",
            $"{{\"file_id\":\"{document.FileId}\",\"curve\":\"RO\",\"condition\":\"gt\",\"value\":6900}}",
            CancellationToken.None);
        True(found.Ok, found.Error ?? found.Json);
        System.Text.Json.Nodes.JsonNode result = System.Text.Json.Nodes.JsonNode.Parse(found.Json)!["result"]!;
        Equal(result["found"]!.GetValue<int>(), 1);
        Near(result["intervals"]![0]!["top"]!.GetValue<double>(), 100.1);

        ToolResult wrong = await tools.CallAsync("curve_stats", "{\"file_id\":\"нет\",\"curves\":[\"RO\"]}", CancellationToken.None);
        True(!wrong.Ok && wrong.Error!.Contains("не открыт"), wrong.Json);

        ToolResult broken = await tools.CallAsync("curve_stats", "{не json", CancellationToken.None);
        True(!broken.Ok, "неверный JSON аргументов принят");
    }
    finally
    {
        File.Delete(path);
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

static void Same(IEnumerable<string> actual, string[] expected)
{
    string[] list = actual.ToArray();
    if (!list.SequenceEqual(expected))
    {
        throw new InvalidOperationException($"ожидалось [{string.Join(", ", expected)}], получено [{string.Join(", ", list)}]");
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

/// <summary>Сайт-посредник в памяти: отвечает тем, что вернёт обработчик.</summary>
internal sealed class FakeProxy(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Sse(params string[] lines) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("\n\n", lines) + "\n\n", System.Text.Encoding.UTF8, "text/event-stream")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request, body);
    }
}

internal sealed class FakeTools : IAgentToolExecutor
{
    public List<string> Calls { get; } = [];

    public Action? OnCall { get; init; }

    public Task<System.Text.Json.Nodes.JsonArray> GetToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "function",
                ["function"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "curve_stats", ["parameters"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" } }
            }));

    public Task<ToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        Calls.Add(name + " " + argumentsJson);
        OnCall?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolResult(true, "{\"ok\":true,\"result\":{\"mean\":12}}"));
    }
}

internal sealed class RecordingObserver : IAgentObserver
{
    public List<string> Events { get; } = [];

    public void TextDelta(string text) => Events.Add("text " + text);

    public void ToolStarted(ToolCall call) => Events.Add("tool+ " + call.Name);

    public void ToolFinished(ToolCall call, bool ok, string summary) => Events.Add((ok ? "tool✓ " : "tool✗ ") + call.Name);

    public void StepFinished(ChatUsage? usage)
    {
    }

    public void Notice(string text) => Events.Add("notice " + text);
}
