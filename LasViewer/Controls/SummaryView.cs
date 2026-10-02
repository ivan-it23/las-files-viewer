using System.Globalization;
using AMK.LasViewer.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>Сводка о файле: как он прочитан, замечания разборщика и статистика кривых.</summary>
public sealed partial class SummaryView : UserControl
{
    private static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));
    private static readonly SolidColorBrush RuleBrush = new(Color.FromArgb(0xFF, 0xE2, 0xE2, 0xE2));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromArgb(0xFF, 0x1F, 0x5F, 0xBF));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(0xFF, 0xB2, 0x6B, 0x00));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xC0, 0x1C, 0x28));

    private readonly StackPanel panel = new() { Spacing = 20, Padding = new Thickness(16) };
    private readonly List<FrameworkElement> textBlocks = [];
    private double viewportWidth;

    public SummaryView()
    {
        ScrollViewer scroller = new() { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Страница прокручивается по горизонтали ради широкой таблицы кривых, и
        // текстовые блоки без предела ширины вытянулись бы в одну строку. Их
        // ширина ограничивается видимой областью.
        scroller.SizeChanged += (_, e) =>
        {
            viewportWidth = e.NewSize.Width;
            LimitTextWidth();
        };
        Content = scroller;
    }

    public void Show(LasDocument document)
    {
        panel.Children.Clear();
        textBlocks.Clear();
        FrameworkElement file = FileBlock(document);
        FrameworkElement issues = IssuesBlock(document);
        textBlocks.Add(file);
        textBlocks.Add(issues);
        panel.Children.Add(file);
        panel.Children.Add(issues);
        panel.Children.Add(CurvesBlock(document));
        LimitTextWidth();
    }

    public void Clear()
    {
        panel.Children.Clear();
        textBlocks.Clear();
    }

    private void LimitTextWidth()
    {
        if (viewportWidth <= 0)
        {
            return;
        }

        foreach (FrameworkElement block in textBlocks)
        {
            block.MaxWidth = Math.Max(360, viewportWidth - panel.Padding.Left - panel.Padding.Right);
        }
    }

    private static FrameworkElement FileBlock(LasDocument document)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        LasIndexInfo index = document.Index;
        string encoding = document.EncodingMethod == "heuristic"
            ? $"{document.EncodingDisplay} (определена по содержимому)"
            : document.EncodingDisplay;
        string unit = index.Unit.Length > 0 ? " " + index.Unit : string.Empty;

        List<(string, string)> rows =
        [
            ("Файл", document.Path),
            ("Размер", FormatSize(document.SizeBytes)),
            ("Кодировка", encoding),
            ("Версия LAS", document.Version + (document.Wrap ? ", с переносом строк (WRAP)" : string.Empty)),
            ("Разделитель", document.Delimiter switch { "comma" => "запятая", "tab" => "табуляция", _ => "пробел" }),
            ("NULL", document.NullValue?.ToString("R", culture) ?? "не задан"),
            ("Строк данных", document.RowCount.ToString("N0", culture)),
            ("Кривых", (document.Curves.Count - 1).ToString(culture) + " и индекс " + index.Mnemonic),
            ("Интервал", $"{Format(index.Start)} – {Format(index.Stop)}{unit}"),
            ("Шаг", Format(index.Step) + unit + DirectionText(index.Direction)),
            ("По заголовку", $"STRT {Format(index.HeaderStart)}, STOP {Format(index.HeaderStop)}, STEP {Format(index.HeaderStep)}")
        ];

        Grid table = new() { ColumnSpacing = 16 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < rows.Count; row++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(table, row, 0, rows[row].Item1, MutedBrush);
            AddCell(table, row, 1, rows[row].Item2, null, selectable: true);
        }

        return Section("Файл", table);
    }

    private static FrameworkElement IssuesBlock(LasDocument document)
    {
        StackPanel list = new() { Spacing = 6 };
        if (document.Issues.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Файл прочитан без замечаний.", Foreground = MutedBrush });
        }

        foreach (LasIssue issue in document.Issues.OrderByDescending(issue => issue.Level).ThenBy(issue => issue.Line ?? 0))
        {
            (string glyph, SolidColorBrush brush, string level) = issue.Level switch
            {
                LasIssueLevel.Error => ("", ErrorBrush, "Ошибка"),
                LasIssueLevel.Warning => ("", WarningBrush, "Предупреждение"),
                _ => ("", InfoBrush, "Сведения")
            };

            Grid row = new() { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FontIcon icon = new() { Glyph = glyph, FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            ToolTipService.SetToolTip(icon, level);
            row.Children.Add(icon);
            AddCell(row, 0, 1, issue.Message, null, selectable: true);
            if (issue.Line is { } line)
            {
                AddCell(row, 0, 2, $"строка {line}", MutedBrush);
            }

            list.Children.Add(row);
        }

        return Section($"Замечания при чтении ({document.Issues.Count})", list);
    }

    private static UIElement CurvesBlock(LasDocument document)
    {
        string[] headers = ["Мнемоника", "Ед.", "Значений", "Пропусков", "Мин.", "Макс.", "Среднее", "Описание"];
        double[] widths = [130, 70, 80, 80, 90, 90, 90, 360];

        Grid table = new() { ColumnSpacing = 12 };
        foreach (double width in widths)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < headers.Length; column++)
        {
            AddCell(table, 0, column, headers[column], MutedBrush, bold: true, alignRight: column is >= 2 and <= 6);
        }

        int row = 1;
        foreach (LasCurve curve in document.Curves)
        {
            CurveStatistics stats = curve.Statistics;
            string[] values =
            [
                curve.Mnemonic == curve.OriginalMnemonic ? curve.Mnemonic : $"{curve.Mnemonic} ({curve.OriginalMnemonic})",
                curve.Unit,
                stats.Count.ToString("N0", CultureInfo.CurrentCulture),
                stats.Nulls.ToString("N0", CultureInfo.CurrentCulture),
                Format(stats.Min),
                Format(stats.Max),
                Format(stats.Mean),
                curve.Description
            ];

            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Border rule = new() { BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(rule, row);
            Grid.SetColumnSpan(rule, headers.Length);
            table.Children.Add(rule);

            for (int column = 0; column < values.Length; column++)
            {
                SolidColorBrush? brush = column == 3 && stats.Nulls > 0 ? WarningBrush : null;
                AddCell(table, row, column, values[column], brush, selectable: true, alignRight: column is >= 2 and <= 6);
            }

            row++;
        }

        return Section("Кривые", table);
    }

    private static StackPanel Section(string title, UIElement content)
    {
        StackPanel section = new() { Spacing = 8 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        section.Children.Add(content);
        return section;
    }

    private static void AddCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush? foreground,
        bool selectable = false,
        bool bold = false,
        bool alignRight = false)
    {
        TextBlock cell = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = selectable,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left,
            Padding = new Thickness(0, 3, 0, 3)
        };
        if (foreground is not null)
        {
            cell.Foreground = foreground;
        }

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static string Format(double? value)
    {
        return value is { } number ? number.ToString("G6", CultureInfo.CurrentCulture) : "—";
    }

    private static string DirectionText(IndexDirection direction) => direction switch
    {
        IndexDirection.Increasing => ", по возрастанию",
        IndexDirection.Decreasing => ", по убыванию (запись снизу вверх)",
        IndexDirection.Unordered => ", индекс не упорядочен",
        _ => string.Empty
    };

    private static string FormatSize(long bytes)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        return bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0", culture) + " МБ"
            : (bytes / 1024.0).ToString("0", culture) + " КБ";
    }
}
