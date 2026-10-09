using AMK.LasViewer.Charting;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;

namespace AMK.LasViewer.App.Services;

/// <summary>Открытый в окне файл: документ, данные планшета, шаблон, пометки и последний вид.</summary>
internal sealed class OpenedDocument(LasDocument document) : ITabletContent
{
    public LasDocument Document { get; } = document;

    public string Key => Document.FileId;

    public string Title => Document.Name;

    public string Tooltip => Document.Path;

    /// <summary>Скважина из ~Well (WELL); пусто, если не указана.</summary>
    public string Well => Document.Section(LasSectionKind.Well)?.Items
        .FirstOrDefault(item => item.Mnemonic.Equals("WELL", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

    /// <summary>Этот файл как источник кривых планшета.</summary>
    public TabletSource AsSource(double depthShift = 0) => new()
    {
        Key = Document.FileId,
        Name = System.IO.Path.GetFileNameWithoutExtension(Document.Name),
        Data = TabletData,
        Well = Well,
        DepthShift = depthShift
    };

    public TabletData TabletData { get; } = TabletData.From(document);

    /// <summary>Шаблон планшета этого файла. У каждого файла своя копия: правка одного не меняет другие.</summary>
    public TabletTemplate Template { get; set; } = DefaultTemplateBuilder.Build(document);

    /// <summary>Маркеры и интервалы файла; хранятся в файле проекта.</summary>
    public DocumentAnnotations Annotations { get; set; } = new();

    /// <summary>Участок и масштаб планшета, к которым пользователь вернётся при переключении файлов.</summary>
    public double? ViewTop { get; set; }

    public double? ViewPixelsPerUnit { get; set; }

    /// <summary>Мнемоники шаблона, которых нет в файле (кривые других файлов не в счёт).</summary>
    public IReadOnlyList<string> MissingTemplateCurves() =>
        Template.Tracks.SelectMany(track => track.Curves)
            .Where(curve => curve.Source is null)
            .Select(curve => curve.Mnemonic)
            .Distinct()
            .Where(mnemonic => TabletData.Values(mnemonic) is null)
            .ToList();
}

internal sealed record OpenFailure(string Path, string Message);

/// <summary>
/// Открытые файлы и файл проекта. Чтение — в движке; здесь учёт файлов и
/// признак несохранённых изменений.
/// </summary>
internal sealed class Project(EngineHost engine)
{
    private readonly List<OpenedDocument> documents = [];
    private readonly List<CombinedTablet> combined = [];

    public IReadOnlyList<OpenedDocument> Documents => documents;

    /// <summary>Совместные планшеты проекта.</summary>
    public IReadOnlyList<CombinedTablet> Combined => combined;

    /// <summary>Файл проекта; <c>null</c>, пока проект не сохранён.</summary>
    public string? Path { get; private set; }

    /// <summary>Есть изменения, которых нет в файле проекта.</summary>
    public bool IsDirty { get; private set; }

    public string DisplayName => Path is null ? "Новый проект" : System.IO.Path.GetFileNameWithoutExtension(Path);

    public event EventHandler? StateChanged;

    public void MarkDirty()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public OpenedDocument? FindByFileId(string fileId) =>
        documents.FirstOrDefault(document => document.Document.FileId == fileId);

    public void AddCombined(CombinedTablet tablet)
    {
        combined.Add(tablet);
        MarkDirty();
    }

    public void RemoveCombined(CombinedTablet tablet)
    {
        if (combined.Remove(tablet))
        {
            MarkDirty();
        }
    }

    /// <summary>
    /// Файлы планшета. У планшета файла — сам файл и открытые файлы, на кривые
    /// которых ссылается его шаблон (с той же единицей глубины); у совместного
    /// — его файлы со сдвигами.
    /// </summary>
    public TabletSources SourcesFor(ITabletContent content)
    {
        switch (content)
        {
            case CombinedTablet tablet when tablet.Sources.Count > 0:
                return new TabletSources(null, tablet.Sources.Select(source => source.Document.AsSource(source.DepthShift)));
            case OpenedDocument document:
            {
                TabletSource own = document.AsSource();
                IEnumerable<TabletSource> others = document.Template.Tracks
                    .SelectMany(track => track.Curves)
                    .Select(curve => curve.Source)
                    .OfType<string>()
                    .Distinct()
                    .Select(FindByFileId)
                    .OfType<OpenedDocument>()
                    .Where(other => SameDepthUnit(other, document))
                    .Select(other => other.AsSource());
                return new TabletSources(own, others);
            }
            default:
                throw new InvalidOperationException("У совместного планшета не осталось файлов.");
        }
    }

    public static bool SameDepthUnit(OpenedDocument first, OpenedDocument second) =>
        string.Equals(first.TabletData.IndexUnit.Trim(), second.TabletData.IndexUnit.Trim(), StringComparison.OrdinalIgnoreCase);

    public OpenedDocument? Find(string path)
    {
        string normalized = Normalize(path);
        return documents.FirstOrDefault(document => Normalize(document.Document.Path) == normalized);
    }

    public async Task<OpenedDocument> OpenAsync(string path, CancellationToken cancellationToken)
    {
        LasDocument document = await engine.OpenLasAsync(path, cancellationToken);
        OpenedDocument opened = new(document);
        documents.Add(opened);
        AppLog.Write(
            $"Открыт {path}: {document.RowCount} строк, {document.Curves.Count} кривых, "
            + $"{document.EncodingDisplay}, замечаний {document.Issues.Count}");
        MarkDirty();
        return opened;
    }

    /// <summary>
    /// Закрывает файл. Его кривые убираются с планшетов других файлов и с
    /// совместных планшетов; совместный планшет без файлов удаляется.
    /// </summary>
    public async Task CloseAsync(OpenedDocument document)
    {
        documents.Remove(document);
        string closedKey = document.Document.FileId;
        foreach (OpenedDocument other in documents)
        {
            other.Template = TemplateEditing.RemapSources(other.Template, key => key == closedKey ? null : key);
        }

        foreach (CombinedTablet tablet in combined.ToList())
        {
            tablet.Sources.RemoveAll(source => source.Document == document);
            tablet.Template = TemplateEditing.RemapSources(tablet.Template, key => key == closedKey ? null : key);
            if (tablet.Sources.Count == 0)
            {
                combined.Remove(tablet);
            }
        }

        MarkDirty();
        try
        {
            await engine.CloseLasAsync(document.Document.FileId, CancellationToken.None);
        }
        catch (EngineException exception)
        {
            // Движок мог быть перезапущен и уже не знать этот файл — данные в
            // окне от этого не зависят.
            AppLog.Write($"Закрытие {document.Document.Path} в движке: {exception.Message}");
        }
    }

    public async Task CloseAllAsync()
    {
        foreach (OpenedDocument document in documents.ToList())
        {
            await CloseAsync(document);
        }

        combined.Clear();
        Path = null;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Снимок для записи. Ссылки кривых на файлы и панели записываются
    /// номерами файлов в проекте: идентификаторы движка при следующем
    /// открытии будут другими.
    /// </summary>
    public ProjectSnapshot Snapshot(IReadOnlyList<ITabletContent> panels, bool syncDepth)
    {
        Dictionary<string, string> numbers = documents
            .Select((document, index) => (document.Document.FileId, index))
            .ToDictionary(pair => pair.FileId, pair => pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TabletTemplate ToProject(TabletTemplate template) => TemplateEditing.RemapSources(template, key => numbers.GetValueOrDefault(key));

        ProjectSnapshot snapshot = new() { SyncDepth = syncDepth };
        foreach (OpenedDocument document in documents)
        {
            snapshot.Documents.Add(new ProjectDocumentEntry
            {
                Path = document.Document.Path,
                Template = ToProject(document.Template),
                Annotations = document.Annotations,
                ViewTop = document.ViewTop,
                ViewPixelsPerUnit = document.ViewPixelsPerUnit
            });
        }

        foreach (CombinedTablet tablet in combined)
        {
            ProjectCombinedEntry entry = new()
            {
                Name = tablet.Name,
                Template = ToProject(tablet.Template),
                Annotations = tablet.Annotations,
                ViewTop = tablet.ViewTop,
                ViewPixelsPerUnit = tablet.ViewPixelsPerUnit
            };
            entry.Sources.AddRange(tablet.Sources.Select(source => new ProjectCombinedSource(documents.IndexOf(source.Document), source.DepthShift)));
            snapshot.Combined.Add(entry);
        }

        foreach (ITabletContent panel in panels)
        {
            int index = panel switch
            {
                OpenedDocument document => documents.IndexOf(document),
                CombinedTablet tablet => combined.IndexOf(tablet),
                _ => -1
            };
            if (index >= 0)
            {
                snapshot.Panels.Add(new ProjectPanel(panel is CombinedTablet, index));
            }
        }

        return snapshot;
    }

    public void SaveTo(string path, ProjectSnapshot snapshot)
    {
        TemplateLibrary.WriteAtomically(path, ProjectSerializer.Write(snapshot, path));
        Path = path;
        IsDirty = false;
        AppLog.Write($"Проект сохранён: {path}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Проект открыт из файла; файлы проекта уже прочитаны вызывающим кодом.</summary>
    public void Adopt(string path)
    {
        Path = path;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Describe(Exception exception)
    {
        return exception switch
        {
            EngineRpcException rpc => rpc.RemoteMessage,
            EngineException engineError => engineError.Message,
            _ => exception.Message
        };
    }

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
