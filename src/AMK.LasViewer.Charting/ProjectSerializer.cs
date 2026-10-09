using System.Text.Json;
using System.Text.Json.Nodes;

namespace AMK.LasViewer.Charting;

/// <summary>Открытый в проекте файл: где лежит и как показан.</summary>
public sealed class ProjectDocumentEntry
{
    public required string Path { get; set; }

    /// <summary>Путь относительно файла проекта; <c>null</c>, если файл на другом диске.</summary>
    public string? RelativePath { get; set; }

    public required TabletTemplate Template { get; set; }

    public DocumentAnnotations Annotations { get; set; } = new();

    public double? ViewTop { get; set; }

    public double? ViewPixelsPerUnit { get; set; }
}

/// <summary>Файл совместного планшета: номер файла в проекте и сдвиг по глубине при показе.</summary>
public sealed record ProjectCombinedSource(int Document, double DepthShift);

/// <summary>
/// Совместный планшет: кривые нескольких файлов на одной колонке глубины.
/// У каждой кривой шаблона указан файл — номер в <see cref="ProjectSnapshot.Documents"/>.
/// </summary>
public sealed class ProjectCombinedEntry
{
    public required string Name { get; set; }

    public List<ProjectCombinedSource> Sources { get; init; } = [];

    public required TabletTemplate Template { get; set; }

    public DocumentAnnotations Annotations { get; set; } = new();

    public double? ViewTop { get; set; }

    public double? ViewPixelsPerUnit { get; set; }
}

/// <summary>Планшет на экране: файл или совместный планшет — по номеру в своём списке.</summary>
public readonly record struct ProjectPanel(bool Combined, int Index);

/// <summary>Содержимое файла проекта.</summary>
/// <remarks>
/// Кривые других файлов в шаблонах ссылаются на файл его номером в
/// <see cref="Documents"/> (строкой): идентификаторы движка при каждом
/// открытии новые.
/// </remarks>
public sealed class ProjectSnapshot
{
    public List<ProjectDocumentEntry> Documents { get; init; } = [];

    public List<ProjectCombinedEntry> Combined { get; init; } = [];

    /// <summary>Планшеты рядом, слева направо.</summary>
    public List<ProjectPanel> Panels { get; init; } = [];

    /// <summary>Общая глубина и курсор у планшетов рядом.</summary>
    public bool SyncDepth { get; set; } = true;
}

/// <summary>
/// Файл проекта (.lvproj): список LAS-файлов с их шаблонами, пометками и видом.
/// </summary>
/// <remarks>
/// Сами данные в проект не копируются — он ссылается на LAS-файлы. Путь
/// хранится дважды: полный и относительно проекта. Если папку с проектом и
/// данными перенесли или сетевой диск получил другую букву, файл находится по
/// относительному пути.
/// </remarks>
public static class ProjectSerializer
{
    public const string Format = "lasviewer-project";
    /// <summary>2 — совместные планшеты и кривые других файлов в шаблонах.</summary>
    public const int Version = 2;
    public const string Extension = ".lvproj";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Write(ProjectSnapshot project, string projectPath)
    {
        string projectDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath)) ?? string.Empty;
        JsonArray documents = [];
        foreach (ProjectDocumentEntry entry in project.Documents)
        {
            documents.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["relative_path"] = RelativeTo(projectDirectory, entry.Path),
                ["template"] = TemplateSerializer.TemplateToJson(entry.Template),
                ["annotations"] = TemplateSerializer.AnnotationsToJson(entry.Annotations),
                ["view"] = entry.ViewTop is { } top && entry.ViewPixelsPerUnit is { } scale
                    ? new JsonObject { ["top"] = top, ["pixels_per_unit"] = scale }
                    : null
            });
        }

        JsonArray combined = [];
        foreach (ProjectCombinedEntry entry in project.Combined)
        {
            JsonArray sources = [];
            foreach (ProjectCombinedSource source in entry.Sources)
            {
                sources.Add(new JsonObject { ["document"] = source.Document, ["depth_shift"] = source.DepthShift });
            }

            combined.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["sources"] = sources,
                ["template"] = TemplateSerializer.TemplateToJson(entry.Template),
                ["annotations"] = TemplateSerializer.AnnotationsToJson(entry.Annotations),
                ["view"] = entry.ViewTop is { } top && entry.ViewPixelsPerUnit is { } scale
                    ? new JsonObject { ["top"] = top, ["pixels_per_unit"] = scale }
                    : null
            });
        }

        // Файл — числом (как в версии 1), совместный планшет — {"combined": номер}.
        JsonArray panels = [];
        foreach (ProjectPanel panel in project.Panels)
        {
            panels.Add(panel.Combined ? new JsonObject { ["combined"] = panel.Index } : JsonValue.Create(panel.Index));
        }

        JsonObject root = new()
        {
            ["format"] = Format,
            ["version"] = Version,
            ["documents"] = documents,
            ["combined"] = combined,
            ["layout"] = new JsonObject { ["panels"] = panels, ["sync_depth"] = project.SyncDepth }
        };
        return root.ToJsonString(WriteOptions);
    }

    public static ProjectSnapshot Read(string json)
    {
        JsonObject root = TemplateSerializer.ParseObject(json);
        TemplateSerializer.RequireFormat(root, Format, Version, "проект");

        ProjectSnapshot project = new();
        foreach (JsonObject document in TemplateSerializer.Array(root, "documents"))
        {
            JsonObject? view = document["view"] as JsonObject;
            project.Documents.Add(new ProjectDocumentEntry
            {
                Path = TemplateSerializer.RequiredString(document, "path"),
                RelativePath = document["relative_path"] is JsonValue relative && relative.TryGetValue(out string? text) ? text : null,
                Template = TemplateSerializer.TemplateFromJson(TemplateSerializer.Object(document, "template")),
                Annotations = TemplateSerializer.AnnotationsFromJson(document["annotations"] as JsonObject),
                ViewTop = view is null ? null : TemplateSerializer.Number(view, "top", double.NaN) is var top && !double.IsNaN(top) ? top : null,
                ViewPixelsPerUnit = view is null ? null : TemplateSerializer.Number(view, "pixels_per_unit", double.NaN) is var scale && !double.IsNaN(scale) ? scale : null
            });
        }

        foreach (JsonObject entry in TemplateSerializer.Array(root, "combined"))
        {
            JsonObject? view = entry["view"] as JsonObject;
            ProjectCombinedEntry tablet = new()
            {
                Name = TemplateSerializer.String(entry, "name", "Совместный планшет"),
                Template = TemplateSerializer.TemplateFromJson(TemplateSerializer.Object(entry, "template")),
                Annotations = TemplateSerializer.AnnotationsFromJson(entry["annotations"] as JsonObject),
                ViewTop = view is null ? null : TemplateSerializer.Number(view, "top", double.NaN) is var top && !double.IsNaN(top) ? top : null,
                ViewPixelsPerUnit = view is null ? null : TemplateSerializer.Number(view, "pixels_per_unit", double.NaN) is var scale && !double.IsNaN(scale) ? scale : null
            };
            foreach (JsonObject source in TemplateSerializer.Array(entry, "sources"))
            {
                int document = (int)TemplateSerializer.Number(source, "document", -1);
                if (document >= 0 && document < project.Documents.Count)
                {
                    tablet.Sources.Add(new ProjectCombinedSource(document, TemplateSerializer.Number(source, "depth_shift", 0)));
                }
            }

            project.Combined.Add(tablet);
        }

        if (root["layout"] is JsonObject layout)
        {
            project.SyncDepth = layout["sync_depth"] is not JsonValue sync || !sync.TryGetValue(out bool value) || value;
            foreach (JsonNode? panel in layout["panels"] as JsonArray ?? [])
            {
                if (panel is JsonValue number && number.TryGetValue(out int index) && index >= 0 && index < project.Documents.Count)
                {
                    project.Panels.Add(new ProjectPanel(false, index));
                }
                else if (panel is JsonObject reference && (int)TemplateSerializer.Number(reference, "combined", -1) is var combinedIndex
                         && combinedIndex >= 0 && combinedIndex < project.Combined.Count)
                {
                    project.Panels.Add(new ProjectPanel(true, combinedIndex));
                }
            }
        }

        return project;
    }

    /// <summary>
    /// Где искать файл: сначала по полному пути, затем относительно проекта.
    /// <c>null</c> — не найден ни там, ни там.
    /// </summary>
    public static string? Resolve(ProjectDocumentEntry entry, string projectPath)
    {
        if (File.Exists(entry.Path))
        {
            return entry.Path;
        }

        if (entry.RelativePath is { Length: > 0 } relative)
        {
            string projectDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath)) ?? string.Empty;
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? RelativeTo(string directory, string path)
    {
        if (directory.Length == 0)
        {
            return null;
        }

        string relative = System.IO.Path.GetRelativePath(directory, path);
        // На другом диске GetRelativePath возвращает полный путь — относительного нет.
        return System.IO.Path.IsPathRooted(relative) ? null : relative;
    }
}
