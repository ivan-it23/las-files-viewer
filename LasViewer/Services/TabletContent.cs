using AMK.LasViewer.Charting;

namespace AMK.LasViewer.App.Services;

/// <summary>То, что показывает планшет: файл или совместный планшет нескольких файлов.</summary>
internal interface ITabletContent
{
    /// <summary>Ключ планшета для перетаскивания: идентификатор файла в движке или «combined:…».</summary>
    string Key { get; }

    string Title { get; }

    string Tooltip { get; }

    TabletTemplate Template { get; }

    DocumentAnnotations Annotations { get; }

    double? ViewTop { get; set; }

    double? ViewPixelsPerUnit { get; set; }
}

/// <summary>Окно, которому планшет задаёт вопросы о файлах проекта.</summary>
internal interface ITabletHost
{
    /// <summary>Файлы, из которых рисуется планшет: свой и те, на кривые которых он ссылается.</summary>
    TabletSources SourcesFor(ITabletContent content);

    /// <summary>Открытый файл по идентификатору движка.</summary>
    OpenedDocument? FindDocument(string fileId);

    /// <summary>
    /// Можно ли положить на планшет кривую файла <paramref name="document"/>:
    /// единица глубины та же, а если скважины разные — пользователь согласился.
    /// На совместный планшет файл при этом добавляется.
    /// </summary>
    Task<bool> AcceptSourceAsync(ITabletContent content, OpenedDocument document);

    /// <summary>Сдвиги по глубине файлов совместного планшета — диалог.</summary>
    Task EditDepthShiftsAsync(CombinedTablet tablet);
}

/// <summary>Файл совместного планшета.</summary>
internal sealed class CombinedSource(OpenedDocument document)
{
    public OpenedDocument Document { get; } = document;

    /// <summary>Сдвиг по глубине при показе: «глубина в файле + сдвиг». Файл не меняется.</summary>
    public double DepthShift { get; set; }
}

/// <summary>
/// Совместный планшет: кривые нескольких файлов (например, ЭМК и РК) на
/// одной колонке глубины. Каждая кривая шаблона ссылается на свой файл и
/// рисуется по своим глубинам — без пересчёта на общую сетку.
/// </summary>
internal sealed class CombinedTablet : ITabletContent
{
    public const string KeyPrefix = "combined:";

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Key => KeyPrefix + Id;

    public required string Name { get; set; }

    public List<CombinedSource> Sources { get; } = [];

    public required TabletTemplate Template { get; set; }

    public DocumentAnnotations Annotations { get; set; } = new();

    public double? ViewTop { get; set; }

    public double? ViewPixelsPerUnit { get; set; }

    public string Title => "Совместный: " + Name;

    public string Tooltip => string.Join(Environment.NewLine, Sources.Select(source => source.Document.Document.Path));

    public bool Contains(OpenedDocument document) => Sources.Any(source => source.Document == document);

    /// <summary>
    /// Новый совместный планшет: треки каждого файла (по его текущему шаблону)
    /// подряд и пустой общий трек, куда перетаскиваются кривые разных методов.
    /// </summary>
    public static CombinedTablet Create(string name, IReadOnlyList<OpenedDocument> documents, bool commonTrack)
    {
        TabletTemplate template = new() { Name = name, DepthTrackWidth = documents[0].Template.DepthTrackWidth };
        foreach (OpenedDocument document in documents)
        {
            string key = document.Document.FileId;
            // Кривые своего файла (Source = null) получают ссылку на этот файл.
            // Кривые других файлов с его планшета не переносятся: файлы
            // совместного планшета выбирает пользователь.
            TabletTemplate copy = TemplateEditing.RemapSources(document.Template, _ => null);
            foreach (TrackDefinition track in copy.Tracks)
            {
                track.Curves.ForEach(curve => curve.Source ??= key);
                track.Fills.ForEach(fill => fill.Source ??= key);
                template.Tracks.Add(track);
            }
        }

        if (commonTrack)
        {
            template.Tracks.Add(new TrackDefinition { Scale = TrackScale.Linear(0, 100) });
        }

        CombinedTablet tablet = new() { Name = name, Template = template };
        tablet.Sources.AddRange(documents.Select(document => new CombinedSource(document)));
        return tablet;
    }
}
