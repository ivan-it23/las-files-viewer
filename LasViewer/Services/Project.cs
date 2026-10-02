using AMK.LasViewer.Charting;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;

namespace AMK.LasViewer.App.Services;

/// <summary>Открытый в окне файл: документ, данные планшета, шаблон, пометки и последний вид.</summary>
internal sealed class OpenedDocument(LasDocument document)
{
    public LasDocument Document { get; } = document;

    public TabletData TabletData { get; } = TabletData.From(document);

    /// <summary>Шаблон планшета этого файла. У каждого файла своя копия: правка одного не меняет другие.</summary>
    public TabletTemplate Template { get; set; } = DefaultTemplateBuilder.Build(document);

    /// <summary>Маркеры и интервалы файла; хранятся в файле проекта.</summary>
    public DocumentAnnotations Annotations { get; set; } = new();

    /// <summary>Участок и масштаб планшета, к которым пользователь вернётся при переключении файлов.</summary>
    public double? ViewTop { get; set; }

    public double? ViewPixelsPerUnit { get; set; }

    /// <summary>Мнемоники шаблона, которых нет в файле.</summary>
    public IReadOnlyList<string> MissingTemplateCurves() =>
        Template.Mnemonics.Distinct().Where(mnemonic => TabletData.Values(mnemonic) is null).ToList();
}

internal sealed record OpenFailure(string Path, string Message);

/// <summary>
/// Открытые файлы и файл проекта. Чтение — в движке; здесь учёт файлов и
/// признак несохранённых изменений.
/// </summary>
internal sealed class Project(EngineHost engine)
{
    private readonly List<OpenedDocument> documents = [];

    public IReadOnlyList<OpenedDocument> Documents => documents;

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

    public async Task CloseAsync(OpenedDocument document)
    {
        documents.Remove(document);
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

        Path = null;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снимок для записи: панели перечислены номерами файлов.</summary>
    public ProjectSnapshot Snapshot(IReadOnlyList<OpenedDocument> panels, bool syncDepth)
    {
        ProjectSnapshot snapshot = new() { SyncDepth = syncDepth };
        foreach (OpenedDocument document in documents)
        {
            snapshot.Documents.Add(new ProjectDocumentEntry
            {
                Path = document.Document.Path,
                Template = document.Template,
                Annotations = document.Annotations,
                ViewTop = document.ViewTop,
                ViewPixelsPerUnit = document.ViewPixelsPerUnit
            });
        }

        foreach (OpenedDocument panel in panels)
        {
            int index = documents.IndexOf(panel);
            if (index >= 0)
            {
                snapshot.Panels.Add(index);
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
