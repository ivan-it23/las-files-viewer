using System.Globalization;
using AMK.LasViewer.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>Секции заголовка LAS в порядке файла, со ссылкой на строку каждой записи.</summary>
public sealed partial class HeaderView : UserControl
{
    private static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));
    private static readonly SolidColorBrush RuleBrush = new(Color.FromArgb(0xFF, 0xE2, 0xE2, 0xE2));
    private static readonly SolidColorBrush EmptyValueBrush = new(Color.FromArgb(0xFF, 0xFF, 0xF4, 0xD6));

    private readonly StackPanel panel = new() { Spacing = 18, Padding = new Thickness(16) };

    public HeaderView()
    {
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void Show(LasDocument document)
    {
        panel.Children.Clear();
        foreach (LasSection section in document.Sections)
        {
            if (section.Kind == LasSectionKind.Data)
            {
                continue;
            }

            panel.Children.Add(SectionBlock(section));
        }
    }

    public void Clear() => panel.Children.Clear();

    private static UIElement SectionBlock(LasSection section)
    {
        StackPanel block = new() { Spacing = 6 };
        StackPanel title = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock { Text = "~" + section.Title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = $"{KindName(section.Kind)}, строка {section.Line}",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        block.Children.Add(title);

        if (section.Kind is LasSectionKind.Other or LasSectionKind.Unknown)
        {
            string text = section.Text.Trim('\n', '\r');
            TextBlock body = new()
            {
                Text = text.Length == 0 ? "(пусто)" : text,
                FontFamily = new FontFamily("Consolas"),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };
            // Foreground = null в WinUI не «цвет по умолчанию», а отсутствие кисти:
            // текст становится невидимым. Цвет задаётся только когда он нужен.
            if (text.Length == 0)
            {
                body.Foreground = MutedBrush;
            }

            block.Children.Add(body);
            return block;
        }

        if (section.Items.Count == 0)
        {
            block.Children.Add(new TextBlock { Text = "(пусто)", Foreground = MutedBrush });
            return block;
        }

        Grid table = new() { ColumnSpacing = 16 };
        foreach (double width in new[] { 140.0, 80, 220, 420, 60 })
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        string valueHeader = section.Kind == LasSectionKind.Curve ? "Код API" : "Значение";
        AddRow(table, 0, ["Мнемоника", "Ед.", valueHeader, "Описание", "Строка"], header: true, emptyValue: false);
        int row = 1;
        foreach (LasHeaderItem item in section.Items)
        {
            bool emptyValue = section.Kind == LasSectionKind.Well && item.Value.Length == 0;
            AddRow(
                table,
                row++,
                [item.Mnemonic, item.Unit, item.Value, item.Description, item.Line.ToString(CultureInfo.CurrentCulture)],
                header: false,
                emptyValue);
        }

        block.Children.Add(table);
        return block;
    }

    private static void AddRow(Grid table, int row, string[] values, bool header, bool emptyValue)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Border rule = new()
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = emptyValue ? EmptyValueBrush : null
        };
        Grid.SetRow(rule, row);
        Grid.SetColumnSpan(rule, values.Length);
        table.Children.Add(rule);

        for (int column = 0; column < values.Length; column++)
        {
            TextBlock cell = new()
            {
                Text = values[column],
                Padding = new Thickness(2, 3, 2, 3),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = !header,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = column == 4 ? TextAlignment.Right : TextAlignment.Left
            };
            if (header || column == 4)
            {
                cell.Foreground = MutedBrush;
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            table.Children.Add(cell);
        }
    }

    private static string KindName(LasSectionKind kind) => kind switch
    {
        LasSectionKind.Version => "версия",
        LasSectionKind.Well => "сведения о скважине",
        LasSectionKind.Curve => "кривые",
        LasSectionKind.Parameter => "параметры",
        LasSectionKind.Other => "прочее",
        _ => "нестандартная секция"
    };
}
