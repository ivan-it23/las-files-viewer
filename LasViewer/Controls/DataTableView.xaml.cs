using System.Globalization;
using AMK.LasViewer.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>Таблица значений открытого файла: номер строки и все кривые в порядке файла.</summary>
public sealed partial class DataTableView : UserControl
{
    public DataTableView()
    {
        InitializeComponent();
    }

    public void Show(LasDocument document)
    {
        TableModel model = new(document);
        TableRoot.Width = model.TotalWidth;
        HeaderRow.DataContext = new TableRow(-1, model);
        RowsList.ItemsSource = Enumerable.Range(0, document.RowCount).Select(row => new TableRow(row, model)).ToList();
    }

    public void Clear()
    {
        RowsList.ItemsSource = null;
        HeaderRow.DataContext = null;
    }
}

/// <summary>Столбцы таблицы и их ширины — общие для всех строк.</summary>
public sealed class TableModel
{
    private const double CharacterWidth = 7.2;
    private const double CellPadding = 16;

    public TableModel(LasDocument document)
    {
        Curves = document.Curves;
        Widths = new double[Curves.Count + 1];
        Widths[0] = Math.Max(56, document.RowCount.ToString(CultureInfo.CurrentCulture).Length * CharacterWidth + CellPadding);

        for (int column = 0; column < Curves.Count; column++)
        {
            LasCurve curve = Curves[column];
            int longest = Math.Max(curve.Mnemonic.Length, curve.Unit.Length);
            // Ширина по образцу значений, а не по всем: на длинной записи
            // перебор всех строк ради ширины заметно задержал бы открытие.
            int step = Math.Max(1, curve.Values.Length / 2000);
            for (int row = 0; row < curve.Values.Length; row += step)
            {
                longest = Math.Max(longest, Format(curve.Values[row]).Length);
            }

            Widths[column + 1] = Math.Max(70, longest * CharacterWidth + CellPadding);
        }

        TotalWidth = Widths.Sum() + 12;
    }

    public IReadOnlyList<LasCurve> Curves { get; }

    public double[] Widths { get; }

    public double TotalWidth { get; }

    /// <summary>
    /// Значение без округления: кратчайшая запись, из которой читается то же
    /// число. Таблица показывает то, что записано в файле.
    /// </summary>
    public static string Format(double value)
    {
        return double.IsNaN(value) ? "—" : value.ToString("R", CultureInfo.CurrentCulture);
    }
}

public sealed record TableRow(int Index, TableModel Model);

/// <summary>
/// Строка таблицы. Ячейки создаются один раз на контейнер и при прокрутке
/// только меняют текст — контейнеры ListView переиспользуются.
/// </summary>
public sealed partial class TableRowView : Grid
{
    private static readonly SolidColorBrush NullBrush = new(Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0));
    private static readonly SolidColorBrush NumberBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush HeaderBackground = new(Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
    private static readonly SolidColorBrush TextBrush = new(Colors.Black);

    private readonly List<TextBlock> cells = [];
    private TableModel? builtFor;

    public TableRowView()
    {
        DataContextChanged += (_, args) => Update(args.NewValue as TableRow);
    }

    public bool IsHeader { get; set; }

    private void Update(TableRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!ReferenceEquals(builtFor, row.Model))
        {
            Build(row.Model);
        }

        if (IsHeader)
        {
            return;
        }

        cells[0].Text = (row.Index + 1).ToString(CultureInfo.CurrentCulture);
        for (int column = 0; column < row.Model.Curves.Count; column++)
        {
            double value = row.Model.Curves[column].Values[row.Index];
            TextBlock cell = cells[column + 1];
            cell.Text = TableModel.Format(value);
            cell.Foreground = double.IsNaN(value) ? NullBrush : TextBrush;
        }
    }

    private void Build(TableModel model)
    {
        builtFor = model;
        Children.Clear();
        ColumnDefinitions.Clear();
        cells.Clear();

        if (IsHeader)
        {
            Background = HeaderBackground;
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD0, 0xD0, 0xD0));
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(0, 4, 0, 4);
        }
        else
        {
            Padding = new Thickness(0);
        }

        for (int column = 0; column < model.Widths.Length; column++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(model.Widths[column]) });
            TextBlock cell = new()
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(0, 0, 10, 0)
            };

            if (IsHeader)
            {
                cell.FontWeight = FontWeights.SemiBold;
                cell.TextWrapping = TextWrapping.NoWrap;
                cell.Text = column == 0
                    ? "№"
                    : model.Curves[column - 1].Unit.Length == 0
                        ? model.Curves[column - 1].Mnemonic
                        : $"{model.Curves[column - 1].Mnemonic}\n{model.Curves[column - 1].Unit}";
                ToolTipService.SetToolTip(cell, column == 0 ? "Номер строки данных в файле" : model.Curves[column - 1].Description);
            }
            else if (column == 0)
            {
                cell.Foreground = NumberBrush;
            }

            SetColumn(cell, column);
            Children.Add(cell);
            cells.Add(cell);
        }
    }
}
