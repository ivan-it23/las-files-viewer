using AMK.LasViewer.App.Dialogs;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App;

/// <summary>
/// Совместные планшеты и кривые других файлов на планшете: окно отвечает
/// планшетам, из каких файлов рисовать и можно ли добавить файл.
/// </summary>
public sealed partial class MainWindow : ITabletHost
{
    TabletSources ITabletHost.SourcesFor(ITabletContent content) => project.SourcesFor(content);

    OpenedDocument? ITabletHost.FindDocument(string fileId) => project.FindByFileId(fileId);

    async Task<bool> ITabletHost.AcceptSourceAsync(ITabletContent content, OpenedDocument document)
    {
        TabletSources sources = project.SourcesFor(content);
        if (!string.Equals(sources.IndexUnit.Trim(), document.TabletData.IndexUnit.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await dialogs.ShowMessageAsync(
                "Другая единица глубины",
                $"У {document.Document.Name} глубина в «{document.TabletData.IndexUnit}», а у планшета — в «{sources.IndexUnit}». "
                + "Метры и футы по одному числу совмещать нельзя.");
            return false;
        }

        // Скважина сверяется по WELL: пустое поле не считается расхождением.
        string[] wells = sources.All.Select(source => source.Well).Where(well => well.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (document.Well.Length > 0 && wells.Length > 0 && !wells.Contains(document.Well, StringComparer.CurrentCultureIgnoreCase)
            && !await dialogs.ConfirmAsync(
                "Файлы разных скважин",
                $"{document.Document.Name} — скважина {document.Well}, а на планшете — {string.Join(", ", wells)}. Всё равно совместить?",
                "Совместить",
                "Отмена",
                confirmIsDefault: false))
        {
            return false;
        }

        if (content is CombinedTablet tablet && !tablet.Contains(document))
        {
            tablet.Sources.Add(new CombinedSource(document));
            project.MarkDirty();
            RebuildProjectTree();
        }

        return true;
    }

    async Task ITabletHost.EditDepthShiftsAsync(CombinedTablet tablet) => await EditDepthShiftsAsync(tablet);

    /// <summary>Буфер обмена планшетов: кривые и треки, скопированные для вставки на другие планшеты.</summary>
    private readonly TabletClipboard clipboard = new();

    TabletClipboard ITabletHost.Clipboard => clipboard;

    /// <summary>
    /// Кривая из дерева проекта в буфер — с видом и шкалой, как на планшете
    /// файла, а если её там нет — со шкалой по данным.
    /// </summary>
    private void CopyCurveFromTree(OpenedDocument document, string mnemonic)
    {
        CurveStyle curve = document.Template.Tracks
            .SelectMany(track => track.Curves
                .Where(style => style.Source is null && style.Mnemonic == mnemonic)
                .Select(style => TemplateEditing.CopyCurve(style, track, document.Key)))
            .FirstOrDefault()
            ?? new CurveStyle { Mnemonic = mnemonic, Source = document.Key, Scale = DefaultTemplateBuilder.SuggestScale(document.TabletData, mnemonic) };
        clipboard.Add(new CopiedCurve(curve, document.Document.Name));
        SetStatus($"Кривая {mnemonic} скопирована · в буфере {clipboard.Count}");
    }

    void ITabletHost.Notify(string text) => SetStatus(text);

    private async Task EditDepthShiftsAsync(CombinedTablet tablet)
    {
        if (await CombinedTabletDialogs.EditDepthShiftsAsync(dialogs, tablet))
        {
            Panels.RefreshContent(tablet);
            project.MarkDirty();
            RebuildProjectTree();
            SetStatus($"Сдвиги по глубине применены: {tablet.Name}");
        }
    }

    // ------------------------------------------------------------------
    // Команды
    // ------------------------------------------------------------------

    private async void CombinedTabletMenuItem_Click(object sender, RoutedEventArgs e) => await CreateCombinedTabletAsync();

    private async Task CreateCombinedTabletAsync()
    {
        // Сразу отмечены файлы, показанные рядом, — обычно их и совмещают.
        HashSet<OpenedDocument> preselected = [.. Panels.Documents];
        if (preselected.Count < 2 && active is not null)
        {
            preselected.Add(active);
        }

        CombinedTabletChoice? choice = await CombinedTabletDialogs.CreateAsync(dialogs, project.Documents, preselected);
        if (choice is null)
        {
            return;
        }

        string[] wells = choice.Documents.Select(document => document.Well).Where(well => well.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (wells.Length > 1 && !await dialogs.ConfirmAsync(
                "Файлы разных скважин",
                $"В выбранных файлах разные скважины: {string.Join(", ", wells)}. Всё равно собрать совместный планшет?",
                "Собрать",
                "Отмена",
                confirmIsDefault: false))
        {
            return;
        }

        CombinedTablet tablet = CombinedTablet.Create(choice.Name, choice.Documents, choice.CommonTrack);
        project.AddCombined(tablet);
        ShowCombined(tablet, beside: false);
        SetStatus($"Совместный планшет «{tablet.Name}»: {string.Join(", ", choice.Documents.Select(document => document.Document.Name))}");
    }

    /// <param name="beside">Рядом с уже показанными планшетами, а не вместо планшета в фокусе.</param>
    private void ShowCombined(CombinedTablet tablet, bool beside)
    {
        if (beside)
        {
            Panels.Add(tablet);
        }
        else
        {
            Panels.ShowSingle(tablet);
        }

        // Вкладки «Данные», «Заголовок», «Сводка» показывают файл; у
        // совместного планшета — первый его файл.
        active = tablet.Sources.FirstOrDefault()?.Document ?? active;
        ShowActiveDocument();
    }

    private async Task RenameCombinedAsync(CombinedTablet tablet)
    {
        string? name = await dialogs.PromptAsync("Переименовать совместный планшет", "Название", tablet.Name, "Сохранить");
        if (name is null || name == tablet.Name)
        {
            return;
        }

        tablet.Name = name;
        tablet.Template.Name = name;
        Panels.RefreshContent(tablet);
        project.MarkDirty();
        RebuildProjectTree();
        UpdateTitle();
    }

    private async Task DeleteCombinedAsync(CombinedTablet tablet)
    {
        if (!await dialogs.ConfirmAsync(
                "Удалить совместный планшет",
                $"Удалить совместный планшет «{tablet.Name}» из проекта? Файлы остаются открытыми и не меняются.",
                "Удалить",
                "Отмена",
                confirmIsDefault: false))
        {
            return;
        }

        Panels.Remove(tablet);
        project.RemoveCombined(tablet);
        if (Panels.Count == 0 && active is not null)
        {
            Panels.ShowSingle(active);
        }

        ShowActiveDocument();
        SetStatus($"Совместный планшет «{tablet.Name}» удалён");
    }

    /// <summary>Меню узла совместного планшета в дереве проекта.</summary>
    private MenuFlyout CombinedMenu(CombinedTablet tablet)
    {
        MenuFlyout menu = new();
        menu.Items.Add(MenuItem("Показать на планшете", () => ShowCombined(tablet, beside: false)));
        MenuFlyoutItem beside = MenuItem("Показать рядом", () => ShowCombined(tablet, beside: true));
        beside.IsEnabled = !Panels.Contains(tablet);
        menu.Items.Add(beside);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Сдвиг файлов по глубине…", async () => await EditDepthShiftsAsync(tablet)));
        menu.Items.Add(MenuItem("Переименовать…", async () => await RenameCombinedAsync(tablet)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Удалить совместный планшет", async () => await DeleteCombinedAsync(tablet)));
        return menu;
    }

    /// <summary>После закрытия файла: убрать с экрана совместные планшеты, у которых не осталось файлов, и перерисовать остальные.</summary>
    private void AfterDocumentClosed()
    {
        foreach (CombinedTablet tablet in Panels.Contents.OfType<CombinedTablet>().Where(tablet => !project.Combined.Contains(tablet)).ToList())
        {
            Panels.Remove(tablet);
        }

        Panels.RefreshAll();
    }

    /// <summary>Узел дерева: совместный планшет или один из его файлов.</summary>
    private sealed record CombinedItem(CombinedTablet Tablet, CombinedSource? Source = null)
    {
        public override string ToString()
        {
            if (Source is null)
            {
                return "▦ " + Tablet.Name;
            }

            string shift = Source.DepthShift == 0
                ? string.Empty
                : $"  (сдвиг {Source.DepthShift.ToString("+0.##;−0.##", System.Globalization.CultureInfo.CurrentCulture)} {Source.Document.TabletData.IndexUnit})";
            return Source.Document.Document.Name + shift;
        }
    }
}
