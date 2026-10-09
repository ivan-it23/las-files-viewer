using System.Diagnostics;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using AMK.LasViewer.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AMK.LasViewer.App;

/// <summary>Файл проекта и библиотека шаблонов.</summary>
public sealed partial class MainWindow
{
    // ------------------------------------------------------------------
    // Проект
    // ------------------------------------------------------------------

    private async void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardChangesAsync())
        {
            await ResetProjectAsync();
            SetStatus("Новый проект");
        }
    }

    private async void OpenProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(ProjectSerializer.Extension);
        StorageFile? file = await FilePickers.PickSingleFileAsync(picker, this, dialogs);
        if (file is not null)
        {
            await OpenProjectAsync(file.Path);
        }
    }

    private async void SaveProjectMenuItem_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: false);

    private async void SaveProjectAsMenuItem_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: true);

    /// <summary>
    /// Можно ли закрыть текущий проект: нет изменений, пользователь сохранил их
    /// или согласился потерять. <c>false</c> — пользователь передумал.
    /// </summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!project.IsDirty || project.Documents.Count == 0)
        {
            return true;
        }

        ContentDialogResult answer = await dialogs.AskAsync(
            "Сохранить проект?",
            $"В проекте «{project.DisplayName}» есть несохранённые изменения: открытые файлы, шаблоны "
            + "планшетов, маркеры и интервалы. Сохранить их перед закрытием?",
            "Сохранить",
            "Не сохранять");
        return answer switch
        {
            ContentDialogResult.Primary => await SaveProjectAsync(saveAs: false),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        if (project.Documents.Count == 0)
        {
            await dialogs.ShowMessageAsync("Сохранение проекта", "В проекте нет открытых файлов — сохранять нечего.");
            return false;
        }

        string? path = project.Path;
        if (saveAs || path is null)
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = project.Path is null
                    ? Path.GetFileNameWithoutExtension(project.Documents[0].Document.Name)
                    : project.DisplayName
            };
            picker.FileTypeChoices.Add("Проект просмотрщика LAS", [ProjectSerializer.Extension]);
            StorageFile? file = await FilePickers.PickSaveFileAsync(picker, this, dialogs);
            if (file is null)
            {
                return false;
            }

            path = file.Path;
        }

        try
        {
            Panels.SaveViews();
            project.SaveTo(path, project.Snapshot(Panels.Contents, Panels.SyncDepth));
            SetStatus($"Проект сохранён: {path}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Проект не сохранён в {path}: {exception}");
            await dialogs.ShowMessageAsync("Проект не сохранён", $"{path}\n\n{exception.Message}");
            return false;
        }
    }

    private async Task ResetProjectAsync()
    {
        Panels.Clear();
        active = null;
        shownDetails = null;
        await project.CloseAllAsync();
        ShowActiveDocument();
    }

    private async Task OpenProjectAsync(string path)
    {
        if (!await EnsureEngineReadyAsync())
        {
            return;
        }

        ProjectSnapshot snapshot;
        try
        {
            snapshot = ProjectSerializer.Read(await File.ReadAllTextAsync(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            await dialogs.ShowMessageAsync("Проект не открыт", $"{path}\n\n{exception.Message}");
            return;
        }

        await ResetProjectAsync();
        List<OpenFailure> failures = [];
        List<string> mismatched = [];
        Dictionary<int, OpenedDocument> opened = [];

        for (int index = 0; index < snapshot.Documents.Count; index++)
        {
            ProjectDocumentEntry entry = snapshot.Documents[index];
            string? resolved = ProjectSerializer.Resolve(entry, path);
            if (resolved is null)
            {
                failures.Add(new OpenFailure(entry.Path, "Файл не найден ни по сохранённому пути, ни рядом с проектом."));
                continue;
            }

            SetStatus($"Открытие {Path.GetFileName(resolved)}...");
            try
            {
                OpenedDocument document = await project.OpenAsync(resolved, lifetime.Token);
                document.Template = entry.Template;
                document.Annotations = entry.Annotations;
                document.ViewTop = entry.ViewTop;
                document.ViewPixelsPerUnit = entry.ViewPixelsPerUnit;
                opened[index] = document;

                IReadOnlyList<string> missing = document.MissingTemplateCurves();
                if (missing.Count > 0)
                {
                    mismatched.Add($"{document.Document.Name}: нет кривых {string.Join(", ", missing)}");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (EngineException exception)
            {
                failures.Add(new OpenFailure(resolved, Project.Describe(exception)));
            }
        }

        // Кривые других файлов ссылаются на файл номером в проекте; теперь,
        // когда файлы открыты, номер заменяется идентификатором движка.
        // Кривые файлов, которые не открылись, убираются.
        string? FileIdOf(string number) =>
            int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            && opened.TryGetValue(index, out OpenedDocument? document)
                ? document.Document.FileId
                : null;

        foreach (OpenedDocument document in opened.Values)
        {
            document.Template = TemplateEditing.RemapSources(document.Template, FileIdOf);
        }

        List<CombinedTablet?> combined = [];
        foreach (ProjectCombinedEntry entry in snapshot.Combined)
        {
            List<CombinedSource> sources = entry.Sources
                .Where(source => opened.ContainsKey(source.Document))
                .Select(source => new CombinedSource(opened[source.Document]) { DepthShift = source.DepthShift })
                .ToList();
            if (sources.Count == 0)
            {
                combined.Add(null);
                continue;
            }

            CombinedTablet tablet = new()
            {
                Name = entry.Name,
                Template = TemplateEditing.RemapSources(entry.Template, FileIdOf),
                Annotations = entry.Annotations,
                ViewTop = entry.ViewTop,
                ViewPixelsPerUnit = entry.ViewPixelsPerUnit
            };
            tablet.Sources.AddRange(sources);
            project.AddCombined(tablet);
            combined.Add(tablet);
        }

        List<ITabletContent> panels = [];
        foreach (ProjectPanel panel in snapshot.Panels)
        {
            ITabletContent? content = panel.Combined
                ? combined.ElementAtOrDefault(panel.Index)
                : opened.GetValueOrDefault(panel.Index);
            if (content is not null)
            {
                panels.Add(content);
            }
        }

        if (panels.Count == 0 && project.Documents.Count > 0)
        {
            panels.Add(project.Documents[0]);
        }

        Panels.SyncDepth = snapshot.SyncDepth;
        SyncCheck.IsChecked = snapshot.SyncDepth;
        Panels.SetLayout(panels);
        active = Panels.FocusedDocument ?? (Panels.FocusedContent as CombinedTablet)?.Sources[0].Document ?? project.Documents.FirstOrDefault();
        project.Adopt(path);
        ShowActiveDocument();
        SetStatus($"Открыт проект {project.DisplayName}: файлов {project.Documents.Count}");

        await ReportFailuresAsync(failures);
        if (mismatched.Count > 0)
        {
            await dialogs.ShowMessageAsync(
                "Файлы изменились после сохранения проекта",
                "В шаблонах есть кривые, которых в файлах больше нет. На планшете они помечены «нет в файле».\n\n"
                + string.Join("\n", mismatched));
        }
    }

    // ------------------------------------------------------------------
    // Шаблоны
    // ------------------------------------------------------------------

    private void RebuildTemplateMenu()
    {
        TemplateMenu.Items.Clear();
        IReadOnlyList<LibraryTemplate> library = TemplateLibrary.List();

        TemplateMenu.Items.Add(MenuItem("Сохранить шаблон в библиотеку…", async () => await SaveTemplateToLibraryAsync()));

        MenuFlyoutSubItem apply = new() { Text = "Применить к файлу" };
        MenuFlyoutSubItem applyAll = new() { Text = "Применить ко всем на планшете" };
        foreach (MenuFlyoutSubItem target in new[] { apply, applyAll })
        {
            bool all = target == applyAll;
            target.Items.Add(MenuItem(DefaultTemplateBuilder.AutomaticName, async () => await ApplyTemplateAsync(null, all)));
            if (library.Count > 0)
            {
                target.Items.Add(new MenuFlyoutSeparator());
            }

            foreach (LibraryTemplate template in library)
            {
                target.Items.Add(MenuItem(template.Name, async () => await ApplyTemplateAsync(template.Path, all)));
            }
        }

        TemplateMenu.Items.Add(apply);
        TemplateMenu.Items.Add(applyAll);

        if (library.Count > 0)
        {
            MenuFlyoutSubItem delete = new() { Text = "Удалить из библиотеки" };
            foreach (LibraryTemplate template in library)
            {
                delete.Items.Add(MenuItem(template.Name, async () => await DeleteTemplateAsync(template)));
            }

            TemplateMenu.Items.Add(delete);
        }

        TemplateMenu.Items.Add(new MenuFlyoutSeparator());
        TemplateMenu.Items.Add(MenuItem("Экспорт шаблона в файл…", async () => await ExportTemplateAsync()));
        TemplateMenu.Items.Add(MenuItem("Импорт шаблона из файла…", async () => await ImportTemplateAsync()));
        TemplateMenu.Items.Add(MenuItem("Открыть папку библиотеки", () =>
        {
            Directory.CreateDirectory(TemplateLibrary.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { TemplateLibrary.Directory } });
        }));
    }

    private static MenuFlyoutItem MenuItem(string text, Action action)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task<bool> RequireActiveAsync()
    {
        if (Panels.FocusedContent is CombinedTablet)
        {
            // Шаблон описывает треки одного файла; для совместного планшета
            // шаблоны будут позже.
            await dialogs.ShowMessageAsync("Шаблон", "Шаблоны относятся к планшету одного файла. Для совместного планшета они пока не поддерживаются.");
            return false;
        }

        if (active is not null)
        {
            return true;
        }

        await dialogs.ShowMessageAsync("Шаблон", "Сначала откройте LAS-файл: шаблон относится к планшету файла.");
        return false;
    }

    private async Task SaveTemplateToLibraryAsync()
    {
        if (!await RequireActiveAsync())
        {
            return;
        }

        string suggested = active!.Template.Name is { Length: > 0 } name && name != DefaultTemplateBuilder.AutomaticName
            ? name
            : Path.GetFileNameWithoutExtension(active.Document.Name);
        string? chosen = await dialogs.PromptAsync("Сохранить шаблон", "Название шаблона", suggested, "Сохранить");
        if (chosen is null)
        {
            return;
        }

        if (TemplateLibrary.List().Any(template => string.Equals(template.Name, chosen, StringComparison.CurrentCultureIgnoreCase))
            && !await dialogs.ConfirmAsync("Шаблон уже есть", $"Шаблон «{chosen}» уже есть в библиотеке. Заменить его?", "Заменить", "Отмена", confirmIsDefault: false))
        {
            return;
        }

        try
        {
            TemplateLibrary.Save(active.Template, chosen);
            active.Template.Name = chosen;
            RebuildTemplateMenu();
            SetStatus($"Шаблон «{chosen}» сохранён в библиотеку");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Шаблон не сохранён", exception.Message);
        }
    }

    /// <summary>Применяет шаблон из библиотеки (или автоматический при <c>null</c>).</summary>
    private async Task ApplyTemplateAsync(string? path, bool allPanels)
    {
        if (!await RequireActiveAsync())
        {
            return;
        }

        TabletTemplate? template = null;
        if (path is not null)
        {
            try
            {
                template = TemplateLibrary.Load(path);
            }
            catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
            {
                await dialogs.ShowMessageAsync("Шаблон не прочитан", $"{path}\n\n{exception.Message}");
                return;
            }
        }

        IReadOnlyList<OpenedDocument> targets = allPanels ? Panels.Documents : [active!];
        await ApplyTemplateToAsync(targets, template);
    }

    private async Task ApplyTemplateToAsync(IReadOnlyList<OpenedDocument> targets, TabletTemplate? template)
    {
        List<string> mismatched = [];
        foreach (OpenedDocument document in targets)
        {
            document.Template = template?.Clone() ?? DefaultTemplateBuilder.Build(document.Document);
            Panels.RefreshContent(document);
            IReadOnlyList<string> missing = document.MissingTemplateCurves();
            if (missing.Count > 0)
            {
                mismatched.Add($"{document.Document.Name}: {string.Join(", ", missing)}");
            }
        }

        project.MarkDirty();
        SetStatus($"Шаблон «{template?.Name ?? DefaultTemplateBuilder.AutomaticName}» применён");
        if (mismatched.Count > 0)
        {
            await dialogs.ShowMessageAsync(
                "Не все кривые шаблона есть в файле",
                "Эти кривые на планшете помечены «нет в файле». Их можно убрать с трека или заменить "
                + "кривыми файла через меню трека.\n\n" + string.Join("\n", mismatched));
        }
    }

    private async Task DeleteTemplateAsync(LibraryTemplate template)
    {
        if (!await dialogs.ConfirmAsync("Удалить шаблон", $"Удалить шаблон «{template.Name}» из библиотеки?", "Удалить", "Отмена", confirmIsDefault: false))
        {
            return;
        }

        try
        {
            TemplateLibrary.Delete(template);
            RebuildTemplateMenu();
            SetStatus($"Шаблон «{template.Name}» удалён");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Шаблон не удалён", exception.Message);
        }
    }

    private async Task ExportTemplateAsync()
    {
        if (!await RequireActiveAsync())
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = active!.Template.Name is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(active.Document.Name)
        };
        picker.FileTypeChoices.Add("Шаблон планшета", [TemplateLibrary.Extension]);
        StorageFile? file = await FilePickers.PickSaveFileAsync(picker, this, dialogs);
        if (file is null)
        {
            return;
        }

        try
        {
            TemplateLibrary.Export(active.Template, file.Path);
            SetStatus($"Шаблон сохранён: {file.Path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Шаблон не сохранён", exception.Message);
        }
    }

    private async Task ImportTemplateAsync()
    {
        if (!await RequireActiveAsync())
        {
            return;
        }

        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(TemplateLibrary.Extension);
        StorageFile? file = await FilePickers.PickSingleFileAsync(picker, this, dialogs);
        if (file is null)
        {
            return;
        }

        TabletTemplate template;
        try
        {
            template = TemplateLibrary.Load(file.Path);
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Шаблон не прочитан", $"{file.Path}\n\n{exception.Message}");
            return;
        }

        await ApplyTemplateToAsync([active!], template);
    }
}
