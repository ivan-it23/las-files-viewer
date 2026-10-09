using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Разметка ответов агента: заголовки, списки, таблицы, блоки кода, жирный,
/// курсив и код в строке. Полный Markdown не нужен — модели велено писать
/// коротко, а этого набора хватает, чтобы ответ читался.
/// </summary>
internal static partial class MarkdownView
{
    private static readonly SolidColorBrush CodeBackground = new(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
    private static readonly SolidColorBrush TableBorder = new(Color.FromArgb(0xFF, 0xD6, 0xD6, 0xD6));
    private static readonly SolidColorBrush TableHeader = new(Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFA));
    private static readonly FontFamily Monospace = new("Consolas");

    public static FrameworkElement Render(string markdown)
    {
        StackPanel root = new() { Spacing = 6 };
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                List<string> code = [];
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }

                index++;
                root.Children.Add(new Border
                {
                    Background = CodeBackground,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = new TextBlock
                    {
                        Text = string.Join("\n", code),
                        FontFamily = Monospace,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                });
                continue;
            }

            if (trimmed.StartsWith('|') && index + 1 < lines.Length && TableSeparator().IsMatch(lines[index + 1].Trim()))
            {
                List<string> rows = [trimmed];
                index += 2;
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    rows.Add(lines[index].Trim());
                    index++;
                }

                root.Children.Add(Table(rows));
                continue;
            }

            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                RichTextBlock block = Text(heading.Groups[2].Value);
                block.FontWeight = FontWeights.SemiBold;
                block.FontSize = heading.Groups[1].Value.Length <= 2 ? 16 : 14;
                block.Margin = new Thickness(0, 4, 0, 0);
                root.Children.Add(block);
                index++;
                continue;
            }

            if (ListItem().IsMatch(line))
            {
                StackPanel list = new() { Spacing = 2 };
                while (index < lines.Length && ListItem().Match(lines[index]) is { Success: true } next)
                {
                    int indent = next.Groups[1].Value.Length;
                    string marker = char.IsDigit(next.Groups[2].Value[0]) ? next.Groups[2].Value : "•";
                    Grid row = new() { Margin = new Thickness(Math.Min(indent, 8) * 6, 0, 0, 0), ColumnSpacing = 6 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = marker, FontSize = 13 });
                    RichTextBlock text = Text(next.Groups[3].Value);
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    list.Children.Add(row);
                    index++;
                }

                root.Children.Add(list);
                continue;
            }

            // Абзац — подряд идущие строки до пустой или до другого блока.
            List<string> paragraph = [trimmed];
            index++;
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal)
                   && !lines[index].Trim().StartsWith('|')
                   && !Heading().IsMatch(lines[index].Trim())
                   && !ListItem().IsMatch(lines[index]))
            {
                paragraph.Add(lines[index].Trim());
                index++;
            }

            root.Children.Add(Text(string.Join(" ", paragraph)));
        }

        return root;
    }

    private static RichTextBlock Text(string text)
    {
        RichTextBlock block = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
        Paragraph paragraph = new();
        foreach (Inline inline in Inlines(text))
        {
            paragraph.Inlines.Add(inline);
        }

        block.Blocks.Add(paragraph);
        return block;
    }

    /// <summary>Жирный (**…**), курсив (*…* или _…_) и код (`…`).</summary>
    private static IEnumerable<Inline> Inlines(string text)
    {
        int position = 0;
        foreach (Match match in InlineMarkup().Matches(text))
        {
            if (match.Index > position)
            {
                yield return new Run { Text = text[position..match.Index] };
            }

            if (match.Groups["bold"].Success)
            {
                Bold bold = new();
                bold.Inlines.Add(new Run { Text = match.Groups["bold"].Value });
                yield return bold;
            }
            else if (match.Groups["code"].Success)
            {
                yield return new Run { Text = match.Groups["code"].Value, FontFamily = Monospace };
            }
            else
            {
                Italic italic = new();
                italic.Inlines.Add(new Run { Text = match.Groups["italic"].Value });
                yield return italic;
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            yield return new Run { Text = text[position..] };
        }
    }

    private static FrameworkElement Table(List<string> rows)
    {
        List<string[]> cells = rows
            .Select(row => row.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray())
            .ToList();
        int columns = cells.Max(row => row.Length);

        Grid grid = new() { BorderBrush = TableBorder, BorderThickness = new Thickness(1, 1, 0, 0) };
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (int row = 0; row < cells.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int column = 0; column < columns; column++)
            {
                string value = column < cells[row].Length ? cells[row][column] : string.Empty;
                RichTextBlock text = Text(value);
                text.FontSize = 12;
                if (row == 0)
                {
                    text.FontWeight = FontWeights.SemiBold;
                }

                Border border = new()
                {
                    BorderBrush = TableBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Background = row == 0 ? TableHeader : null,
                    Child = text
                };
                Grid.SetRow(border, row);
                Grid.SetColumn(border, column);
                grid.Children.Add(border);
            }
        }

        // Широкая таблица прокручивается, а не сжимает панель.
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*•]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|(?<![\w*])[*_](?<italic>[^*_\s][^*_]*?)[*_](?![\w*])")]
    private static partial Regex InlineMarkup();
}
