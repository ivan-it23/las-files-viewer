using AMK.LasViewer.Charting;
using AMK.LasViewer.Core;
using AMK.LasViewer.Engine;

namespace AMK.LasViewer.App.Services;

/// <summary>Открытый в окне файл: документ, данные планшета и последний вид.</summary>
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
}

internal sealed record OpenFailure(string Path, string Message);

/// <summary>Набор открытых файлов. Чтение — в движке, здесь только учёт.</summary>
internal sealed class Project(EngineHost engine)
{
    private readonly List<OpenedDocument> documents = [];

    public IReadOnlyList<OpenedDocument> Documents => documents;

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
        return opened;
    }

    public async Task CloseAsync(OpenedDocument document)
    {
        documents.Remove(document);
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
            return Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
