using AMK.LasViewer.Core;

namespace AMK.LasViewer.Charting;

/// <summary>
/// Данные документа, подготовленные для планшета: глубины по возрастанию,
/// без строк с пропущенной глубиной, и значения кривых в том же порядке.
/// </summary>
/// <remarks>
/// Планшет и поиск значения под курсором рассчитывают на возрастающий индекс.
/// Файл, записанный снизу вверх, разворачивается здесь, а не в каждом месте
/// отрисовки. Неупорядоченный индекс сортируется — движок уже сообщил о нём
/// в замечаниях к файлу.
/// </remarks>
public sealed class TabletData
{
    private readonly Dictionary<string, double[]> values;
    private readonly Dictionary<string, string> units;

    private TabletData(string indexUnit, double[] depths, Dictionary<string, double[]> values, Dictionary<string, string> units, List<string> mnemonics)
    {
        IndexUnit = indexUnit;
        Depths = depths;
        this.values = values;
        this.units = units;
        Mnemonics = mnemonics;
    }

    /// <summary>Мнемоники кривых (без индекса) в порядке файла.</summary>
    public IReadOnlyList<string> Mnemonics { get; }

    public string IndexUnit { get; }

    public double[] Depths { get; }

    public double Top => Depths.Length > 0 ? Depths[0] : 0;

    public double Bottom => Depths.Length > 0 ? Depths[^1] : 0;

    public double[]? Values(string mnemonic) => values.GetValueOrDefault(mnemonic);

    public string Unit(string mnemonic) => units.GetValueOrDefault(mnemonic, string.Empty);

    public static TabletData From(LasDocument document)
    {
        double[] index = document.IndexCurve.Values;
        int[] order = Enumerable.Range(0, index.Length).Where(row => !double.IsNaN(index[row])).ToArray();

        switch (document.Index.Direction)
        {
            case IndexDirection.Increasing:
                break;
            case IndexDirection.Decreasing:
                Array.Reverse(order);
                break;
            default:
                // Сортировка устойчивая: строки с одинаковой глубиной остаются в порядке файла.
                order = order.OrderBy(row => index[row]).ToArray();
                break;
        }

        double[] depths = order.Select(row => index[row]).ToArray();
        Dictionary<string, double[]> columns = new(StringComparer.Ordinal);
        Dictionary<string, string> units = new(StringComparer.Ordinal);
        List<string> mnemonics = [];
        foreach (LasCurve curve in document.DataCurves)
        {
            mnemonics.Add(curve.Mnemonic);
            double[] source = curve.Values;
            columns[curve.Mnemonic] = order.Select(row => source[row]).ToArray();
            units[curve.Mnemonic] = curve.Unit;
        }

        return new TabletData(document.Index.Unit, depths, columns, units, mnemonics);
    }

    /// <summary>Номер строки, ближайшей по глубине, или −1 для пустых данных.</summary>
    public int NearestRow(double depth) => NearestRow(Depths, depth);

    /// <summary>Номер ближайшей глубины в возрастающем массиве или −1 для пустого.</summary>
    public static int NearestRow(double[] depths, double depth)
    {
        if (depths.Length == 0)
        {
            return -1;
        }

        int position = Array.BinarySearch(depths, depth);
        if (position >= 0)
        {
            return position;
        }

        int next = ~position;
        if (next == 0)
        {
            return 0;
        }

        if (next >= depths.Length)
        {
            return depths.Length - 1;
        }

        return depth - depths[next - 1] <= depths[next] - depth ? next - 1 : next;
    }
}
