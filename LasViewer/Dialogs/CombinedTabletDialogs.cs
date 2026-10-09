using System.Globalization;
using AMK.LasViewer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>Что выбрал пользователь для нового совместного планшета.</summary>
internal sealed record CombinedTabletChoice(string Name, IReadOnlyList<OpenedDocument> Documents, bool CommonTrack);

/// <summary>Диалоги совместного планшета: создание и сдвиг файлов по глубине.</summary>
internal static class CombinedTabletDialogs
{
    /// <param name="preselected">Файлы, отмеченные сразу, — обычно те, что уже показаны рядом.</param>
    public static async Task<CombinedTabletChoice?> CreateAsync(
        DialogService dialogs, IReadOnlyList<OpenedDocument> documents, IReadOnlyCollection<OpenedDocument> preselected)
    {
        if (documents.Count < 2)
        {
            await dialogs.ShowMessageAsync(
                "Совместный планшет",
                "Совместный планшет собирается из нескольких файлов — откройте хотя бы два LAS-файла, например ЭМК и РК.");
            return null;
        }

        Form form = new();
        TextBox name = form.Add("Название", new TextBox { MinWidth = 380 });
        StackPanel list = form.Add("Файлы", new StackPanel { Spacing = 2 });
        List<(CheckBox Box, OpenedDocument Document)> boxes = [];
        foreach (OpenedDocument document in documents)
        {
            Charting.TabletData data = document.TabletData;
            CheckBox box = new()
            {
                Content = $"{document.Document.Name} — {Format(data.Top)}–{Format(data.Bottom)} {data.IndexUnit}"
                          + (document.Well.Length > 0 ? $", скважина {document.Well}" : string.Empty),
                IsChecked = preselected.Contains(document)
            };
            boxes.Add((box, document));
            list.Children.Add(box);
        }

        CheckBox common = form.Add(string.Empty, new CheckBox { Content = "Добавить пустой общий трек для кривых разных файлов", IsChecked = true });
        form.Add(string.Empty, new TextBlock
        {
            Text = "Треки каждого файла берутся с его планшета, в порядке списка. Кривые разных файлов рисуются "
                   + "по своим глубинам — без пересчёта. Кривую на общий трек перетащите с трека файла или из дерева проекта.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            MaxWidth = 460
        });

        IReadOnlyList<OpenedDocument> Selected() => boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Document).ToList();
        string SuggestedName() => string.Join(" + ", Selected().Select(document => Path.GetFileNameWithoutExtension(document.Document.Name)));

        // Название подставляется по выбранным файлам, пока пользователь не ввёл своё.
        bool nameEdited = false;
        name.Text = SuggestedName();
        name.TextChanged += (_, _) => nameEdited = name.FocusState != FocusState.Unfocused || nameEdited;
        foreach ((CheckBox box, _) in boxes)
        {
            box.Click += (_, _) =>
            {
                if (!nameEdited)
                {
                    name.Text = SuggestedName();
                }
            };
        }

        CombinedTabletChoice? result = null;
        ContentDialog dialog = new()
        {
            Title = "Совместный планшет",
            Content = new ScrollViewer { Content = form.Root, MaxHeight = 560 },
            PrimaryButtonText = "Создать",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            IReadOnlyList<OpenedDocument> selected = Selected();
            if (selected.Count < 2)
            {
                form.ShowError("Отметьте хотя бы два файла.");
                args.Cancel = true;
                return;
            }

            string unit = selected[0].TabletData.IndexUnit.Trim();
            if (selected.Any(document => !string.Equals(document.TabletData.IndexUnit.Trim(), unit, StringComparison.OrdinalIgnoreCase)))
            {
                form.ShowError("У файлов разные единицы глубины (" + string.Join(", ", selected.Select(document => document.TabletData.IndexUnit).Distinct())
                               + "): совместить их нельзя.");
                args.Cancel = true;
                return;
            }

            string title = name.Text.Trim();
            result = new CombinedTabletChoice(title.Length > 0 ? title : SuggestedName(), selected, common.IsChecked == true);
        };

        await dialogs.ShowAsync(dialog);
        return result;
    }

    /// <summary>
    /// Сдвиг файлов по глубине. Кривые файла рисуются на глубине «глубина в
    /// файле + сдвиг»; сам файл не меняется.
    /// </summary>
    /// <returns><c>true</c>, если сдвиги изменены.</returns>
    public static async Task<bool> EditDepthShiftsAsync(DialogService dialogs, CombinedTablet tablet)
    {
        string unit = tablet.Sources.Count > 0 ? tablet.Sources[0].Document.TabletData.IndexUnit : string.Empty;
        Form form = new();
        List<(NumberBox Box, CombinedSource Source)> boxes = [];
        foreach (CombinedSource source in tablet.Sources)
        {
            NumberBox box = form.Add($"{source.Document.Document.Name}, {unit}", Form.Number(source.DepthShift, -10000, 10000, 0.1));
            box.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 2 };
            boxes.Add((box, source));
        }

        form.Add(string.Empty, new TextBlock
        {
            Text = "Положительный сдвиг опускает кривые файла (глубина больше), отрицательный — поднимает. "
                   + "Данные файла не меняются: сдвиг хранится в проекте и действует только на этом планшете.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            MaxWidth = 420
        });

        bool changed = false;
        ContentDialog dialog = new()
        {
            Title = $"Сдвиг по глубине: {tablet.Name}",
            Content = new ScrollViewer { Content = form.Root, MaxHeight = 560 },
            PrimaryButtonText = "Применить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (boxes.Any(item => double.IsNaN(item.Box.Value)))
            {
                form.ShowError("Укажите сдвиг для каждого файла (0 — без сдвига).");
                args.Cancel = true;
                return;
            }

            foreach ((NumberBox box, CombinedSource source) in boxes)
            {
                double shift = Math.Round(box.Value, 4);
                changed |= shift != source.DepthShift;
                source.DepthShift = shift;
            }
        };

        await dialogs.ShowAsync(dialog);
        return changed;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
