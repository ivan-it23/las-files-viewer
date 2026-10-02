using AMK.LasViewer.Charting;

namespace AMK.LasViewer.App.Services;

internal sealed record LibraryTemplate(string Name, string Path);

/// <summary>
/// Личная библиотека шаблонов планшета: файлы .lvtpl в
/// <c>%LOCALAPPDATA%\AMK\LasViewer\templates</c>. Чтобы передать шаблон
/// коллеге, его экспортируют в файл.
/// </summary>
internal static class TemplateLibrary
{
    public const string Extension = ".lvtpl";

    public static string Directory { get; } = System.IO.Path.Combine(AppInfo.DataDirectory, "templates");

    public static IReadOnlyList<LibraryTemplate> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        List<LibraryTemplate> templates = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            try
            {
                // Имя берётся из самого шаблона: имя файла может быть искажено
                // заменой недопустимых символов.
                TabletTemplate template = Load(path);
                if (template.Name.Length > 0)
                {
                    name = template.Name;
                }
            }
            catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
            {
                AppLog.Write($"Шаблон {path} не читается: {exception.Message}");
                continue;
            }

            templates.Add(new LibraryTemplate(name, path));
        }

        return templates.OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static TabletTemplate Load(string path) => TemplateSerializer.ReadTemplate(File.ReadAllText(path));

    /// <summary>Сохраняет шаблон под именем; шаблон с тем же именем заменяется.</summary>
    public static string Save(TabletTemplate template, string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        TabletTemplate copy = template.Clone();
        copy.Name = name;
        string path = System.IO.Path.Combine(Directory, SafeFileName(name) + Extension);
        WriteAtomically(path, TemplateSerializer.WriteTemplate(copy));
        return path;
    }

    public static void Export(TabletTemplate template, string path) => WriteAtomically(path, TemplateSerializer.WriteTemplate(template));

    public static void Delete(LibraryTemplate template) => File.Delete(template.Path);

    /// <summary>
    /// Запись через временный файл: при сбое посреди записи прежний файл
    /// остаётся целым, а не обрезанным.
    /// </summary>
    public static void WriteAtomically(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string safe = new(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return safe.Trim().Length == 0 ? "Шаблон" : safe.Trim();
    }
}
