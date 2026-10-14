using System.Text;
using System.Text.RegularExpressions;
using AMK.LasViewer.Agent;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Разметка ответов агента: заголовки разделов, списки, таблицы, выноски
/// (<c>&gt; …</c>), блоки кода, разделители, жирный, курсив, код в строке и
/// экранирование (<c>\*</c>). Формулы LaTeX переводятся в обычный текст
/// (<see cref="MathText"/>): панель их не рисует. Полный Markdown не нужен —
/// модели велено писать коротко и этими средствами.
/// </summary>
/// <remarks>
/// Оформление — чтобы ответ читался по структуре, а не сплошным текстом:
/// заголовок раздела с цветной меткой, вводное жирное слово абзаца или пункта
/// — цветом заголовка, таблица с полосами строк и числами по правому краю,
/// предупреждение — жёлтой выноской.
/// </remarks>
internal static partial class MarkdownView
{
    private static readonly SolidColorBrush TextBrush = Brush(0x1F, 0x1F, 0x1F);
    private static readonly SolidColorBrush Accent = Brush(0x0B, 0x5C, 0xAD);
    private static readonly SolidColorBrush AccentSoft = Brush(0xEE, 0xF4, 0xFB);
    private static readonly SolidColorBrush AccentLine = Brush(0xDC, 0xE6, 0xF2);
    private static readonly SolidColorBrush WarningSoft = Brush(0xFF, 0xF7, 0xE6);
    private static readonly SolidColorBrush WarningBar = Brush(0xE3, 0x9A, 0x1E);
    private static readonly SolidColorBrush WarningText = Brush(0x9A, 0x5B, 0x00);
    private static readonly SolidColorBrush CodeForeground = Brush(0x8A, 0x3B, 0x12);
    private static readonly SolidColorBrush CodeBackground = Brush(0xF6, 0xF8, 0xFA);
    private static readonly SolidColorBrush CodeBorder = Brush(0xE1, 0xE4, 0xE8);
    private static readonly SolidColorBrush TableBorder = Brush(0xD6, 0xDC, 0xE3);
    private static readonly SolidColorBrush TableLine = Brush(0xE6, 0xE9, 0xED);
    private static readonly SolidColorBrush TableStripe = Brush(0xFA, 0xFB, 0xFC);
    private static readonly SolidColorBrush RuleBrush = Brush(0xDC, 0xDC, 0xDC);
    private static readonly FontFamily Monospace = new("Consolas");

    private const double BodySize = 13;
    private const double BodyLineHeight = 20;

    /// <summary>Экранированный символ Markdown временно заменяется знаком из личной области Unicode.</summary>
    private const char EscapeBase = '';

    public static FrameworkElement Render(string markdown) =>
        RenderBlocks(Prepare(markdown.Replace("\r\n", "\n")).Split('\n'), Accent);

    /// <param name="lead">Цвет жирного начала абзаца или пункта.</param>
    private static StackPanel RenderBlocks(string[] lines, SolidColorBrush lead)
    {
        StackPanel root = new() { Spacing = 10 };
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
                    BorderBrush = CodeBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
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

            if (trimmed.StartsWith('>'))
            {
                List<string> quote = [];
                while (index < lines.Length && lines[index].Trim().StartsWith('>'))
                {
                    quote.Add(Quote().Replace(lines[index].Trim(), string.Empty));
                    index++;
                }

                root.Children.Add(Callout(quote));
                continue;
            }

            if (HorizontalRule().IsMatch(trimmed))
            {
                root.Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 2, 0, 2) });
                index++;
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                root.Children.Add(Title(heading.Groups[2].Value, heading.Groups[1].Value.Length, first: root.Children.Count == 0));
                index++;
                continue;
            }

            if (ListItem().IsMatch(line))
            {
                StackPanel list = new() { Spacing = 4 };
                while (index < lines.Length && ListItem().Match(lines[index]) is { Success: true } next)
                {
                    int level = Math.Min(next.Groups[1].Value.Replace("\t", "  ").Length / 2, 3);
                    bool numbered = char.IsDigit(next.Groups[2].Value[0]);
                    Grid row = new() { Margin = new Thickness(level * 16, 0, 0, 0), ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(numbered ? 18 : 10) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(numbered
                        ? new TextBlock
                        {
                            Text = Restore(next.Groups[2].Value),
                            FontSize = BodySize,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = lead,
                            LineHeight = BodyLineHeight,
                            HorizontalAlignment = HorizontalAlignment.Right
                        }
                        : new Ellipse
                        {
                            // Точка маркера — на середине первой строки пункта.
                            Width = 5,
                            Height = 5,
                            Fill = level == 0 ? lead : null,
                            Stroke = lead,
                            StrokeThickness = 1,
                            Margin = new Thickness(0, (BodyLineHeight - 5) / 2, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Top
                        });
                    RichTextBlock text = Text(next.Groups[3].Value, lead);
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
                   && !lines[index].Trim().StartsWith('>')
                   && !Heading().IsMatch(lines[index].Trim())
                   && !HorizontalRule().IsMatch(lines[index].Trim())
                   && !ListItem().IsMatch(lines[index]))
            {
                paragraph.Add(lines[index].Trim());
                index++;
            }

            root.Children.Add(Text(string.Join(" ", paragraph), lead));
        }

        return root;
    }

    /// <summary>
    /// Заголовок раздела. Первый и второй уровень — с линией снизу, третий и
    /// ниже — с цветной меткой слева: в узкой панели ответ так легче
    /// просматривать по разделам.
    /// </summary>
    private static FrameworkElement Title(string text, int level, bool first)
    {
        RichTextBlock title = Text(text);
        title.FontWeight = FontWeights.SemiBold;
        Thickness margin = new(0, first ? 0 : 6, 0, 0);
        if (level <= 2)
        {
            title.FontSize = 16;
            title.LineHeight = 22;
            return new Border
            {
                BorderBrush = AccentLine,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 4),
                Margin = margin,
                Child = title
            };
        }

        title.FontSize = 14;
        title.Foreground = Accent;
        Grid row = new() { ColumnSpacing = 8, Margin = margin };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Accent, Margin = new Thickness(0, 2, 0, 2) });
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        return row;
    }

    /// <summary>
    /// Выноска из строк «&gt; …»: замечание — голубая, предупреждение
    /// («Внимание», «Осторожно», «Важно», «Ограничение», «Брак») — жёлтая.
    /// </summary>
    private static FrameworkElement Callout(List<string> lines)
    {
        string start = Restore(string.Join(" ", lines)).TrimStart('*', '_', ' ');
        bool warning = WarningStart().IsMatch(start);
        StackPanel content = RenderBlocks([.. lines], warning ? WarningText : Accent);
        content.Spacing = 6;
        return new Border
        {
            Background = warning ? WarningSoft : AccentSoft,
            BorderBrush = warning ? WarningBar : Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Child = content
        };
    }

    /// <summary>
    /// Вне блоков кода: формулы LaTeX — в текст, экранированные символы
    /// (<c>\*</c>, <c>\|</c>, <c>\_</c>…) — в знаки, которые разметка не трогает.
    /// </summary>
    private static string Prepare(string markdown)
    {
        string[] parts = markdown.Split("```");
        for (int i = 0; i < parts.Length; i += 2)
        {
            parts[i] = Escaped().Replace(MathText.ToPlain(parts[i]), match => ((char)(EscapeBase + match.Groups[1].Value[0])).ToString());
        }

        return string.Join("```", parts);
    }

    private static string Restore(string text)
    {
        if (text.All(c => c < EscapeBase || c > EscapeBase + 0x7F))
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            builder.Append(c >= EscapeBase && c <= EscapeBase + 0x7F ? (char)(c - EscapeBase) : c);
        }

        return builder.ToString();
    }

    /// <param name="lead">
    /// Цвет жирного начала абзаца или пункта («**Где ρк убывает.** …»): это
    /// подзаголовок по смыслу. Без цвета — начало не выделяется.
    /// </param>
    private static RichTextBlock Text(string text, SolidColorBrush? lead = null)
    {
        RichTextBlock block = new()
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = BodySize,
            LineHeight = BodyLineHeight,
            Foreground = TextBrush
        };
        Paragraph paragraph = new();
        foreach (Inline inline in Inlines(text))
        {
            if (lead is not null && paragraph.Inlines.Count == 0 && inline is Bold)
            {
                inline.Foreground = lead;
            }

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
                yield return new Run { Text = Restore(text[position..match.Index]) };
            }

            if (match.Groups["bold"].Success)
            {
                Bold bold = new();
                bold.Inlines.Add(new Run { Text = Restore(match.Groups["bold"].Value) });
                yield return bold;
            }
            else if (match.Groups["code"].Success)
            {
                yield return new Run { Text = Restore(match.Groups["code"].Value), FontFamily = Monospace, Foreground = CodeForeground };
            }
            else
            {
                Italic italic = new();
                italic.Inlines.Add(new Run { Text = Restore(match.Groups["italic"].Value) });
                yield return italic;
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            yield return new Run { Text = Restore(text[position..]) };
        }
    }

    /// <summary>
    /// Таблица: шапка на голубом фоне, строки через одну чуть темнее, только
    /// горизонтальные линии, числа и интервалы глубин — по правому краю.
    /// </summary>
    private static FrameworkElement Table(List<string> rows)
    {
        List<string[]> cells = rows
            .Select(row => row.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray())
            .ToList();
        int columns = cells.Max(row => row.Length);

        Grid grid = new();
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
                text.LineHeight = 18;
                if (row == 0)
                {
                    text.FontWeight = FontWeights.SemiBold;
                    text.Foreground = Accent;
                }
                else if (NumberCell().IsMatch(Restore(value)))
                {
                    text.TextAlignment = TextAlignment.Right;
                }

                Border border = new()
                {
                    BorderBrush = row == 0 ? AccentLine : TableLine,
                    BorderThickness = new Thickness(0, 0, 0, row == cells.Count - 1 ? 0 : 1),
                    Padding = new Thickness(10, 5, 10, 5),
                    Background = row == 0 ? AccentSoft : row % 2 == 0 ? TableStripe : null,
                    Child = text
                };
                Grid.SetRow(border, row);
                Grid.SetColumn(border, column);
                grid.Children.Add(border);
            }
        }

        // Широкая таблица прокручивается, а не сжимает панель.
        return new Border
        {
            BorderBrush = TableBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled
            }
        };
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromArgb(0xFF, red, green, blue));

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*•]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|(?<![\w*])[*_](?<italic>[^*_\s][^*_]*?)[*_](?![\w*])")]
    private static partial Regex InlineMarkup();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|<>~])")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"^>\s?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(Внимание|Осторожно|Важно|Ограничени|Брак|Предупреждени)", RegexOptions.IgnoreCase)]
    private static partial Regex WarningStart();

    /// <summary>Число, процент или интервал глубин: «12,5», «−3», «48 %», «301,2–315,9».</summary>
    [GeneratedRegex(@"^[−–+-]?\d[\d\s\u00A0.,]*(\s*[–—-]\s*\d[\d\s\u00A0.,]*)?\s*%?$")]
    private static partial Regex NumberCell();
}
