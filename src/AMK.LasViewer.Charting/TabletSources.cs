namespace AMK.LasViewer.Charting;

/// <summary>Файл, кривые которого рисует планшет.</summary>
public sealed class TabletSource
{
    /// <summary>Ключ, на который ссылаются <see cref="CurveStyle.Source"/> и <see cref="FillDefinition.Source"/>.</summary>
    public required string Key { get; init; }

    /// <summary>Короткое имя для подписей: имя файла без расширения.</summary>
    public required string Name { get; init; }

    public required TabletData Data { get; init; }

    /// <summary>Скважина из ~Well (WELL); пусто, если не указана.</summary>
    public string Well { get; init; } = string.Empty;

    /// <summary>
    /// Сдвиг по глубине при показе: кривые файла рисуются на глубине
    /// «глубина в файле + сдвиг». Данные файла не меняются.
    /// </summary>
    public double DepthShift { get; init; }
}

/// <summary>Кривая, готовая к отрисовке: глубины (со сдвигом файла) и значения по возрастанию глубины.</summary>
public sealed class TabletSeries(TabletSource source, double[] depths, double[] values, string unit)
{
    public TabletSource Source { get; } = source;

    public double[] Depths { get; } = depths;

    public double[] Values { get; } = values;

    public string Unit { get; } = unit;

    /// <summary>
    /// Значение на ближайшей глубине записи. За пределами записи (дальше
    /// половины шага от крайней точки) — NaN: у кривой другого файла там
    /// замера нет, и показывать крайнее значение было бы неправдой.
    /// </summary>
    public double ValueAt(double depth)
    {
        int row = TabletData.NearestRow(Depths, depth);
        if (row < 0)
        {
            return double.NaN;
        }

        double step = Depths.Length > 1 ? Math.Abs(Depths[^1] - Depths[0]) / (Depths.Length - 1) : 0;
        double tolerance = Math.Max(step * 0.5, 1e-9);
        return depth < Depths[0] - tolerance || depth > Depths[^1] + tolerance ? double.NaN : Values[row];
    }
}

/// <summary>
/// Файлы, из которых планшет берёт кривые. У планшета файла есть свой файл
/// (<see cref="Own"/>) и, возможно, кривые других файлов; у совместного
/// планшета своего файла нет — все кривые ссылаются на источники.
/// </summary>
/// <remarks>
/// Кривая другого файла рисуется по своим глубинам — без пересчёта на сетку
/// планшета, поэтому значения на экране ровно те, что записаны в файле.
/// Все источники одного планшета — в одной единице глубины: метры и футы
/// совмещать нельзя.
/// </remarks>
public sealed class TabletSources
{
    private readonly Dictionary<string, TabletSource> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Mnemonic), TabletSeries?> series = [];
    private readonly Dictionary<string, double[]> shiftedDepths = new(StringComparer.Ordinal);

    public TabletSources(TabletSource? own, IEnumerable<TabletSource> others)
    {
        Own = own;
        List<TabletSource> all = own is null ? [] : [own];
        foreach (TabletSource source in others)
        {
            if (source.Key != own?.Key && byKey.TryAdd(source.Key, source))
            {
                all.Add(source);
            }
        }

        if (own is not null)
        {
            byKey[own.Key] = own;
        }

        if (all.Count == 0)
        {
            throw new ArgumentException("У планшета должен быть хотя бы один файл.", nameof(others));
        }

        All = all;
        Primary = all[0];
        IndexUnit = Primary.Data.IndexUnit;

        double top = double.PositiveInfinity;
        double bottom = double.NegativeInfinity;
        foreach (TabletSource source in all.Where(source => source.Data.Depths.Length > 0))
        {
            top = Math.Min(top, source.Data.Top + source.DepthShift);
            bottom = Math.Max(bottom, source.Data.Bottom + source.DepthShift);
        }

        Top = double.IsFinite(top) ? top : 0;
        Bottom = double.IsFinite(bottom) ? bottom : 0;
    }

    /// <summary>Планшет одного файла без кривых других файлов.</summary>
    public static TabletSources Single(TabletData data, string key = "", string name = "") =>
        new(new TabletSource { Key = key, Name = name, Data = data }, []);

    /// <summary>Файл планшета; <c>null</c> у совместного планшета.</summary>
    public TabletSource? Own { get; }

    public bool IsCombined => Own is null;

    /// <summary>Все файлы: свой первым, затем остальные в порядке добавления.</summary>
    public IReadOnlyList<TabletSource> All { get; }

    /// <summary>Файл, по которому определяются единица и шаг глубины: свой или первый.</summary>
    public TabletSource Primary { get; }

    public string IndexUnit { get; }

    /// <summary>Верх записи по всем файлам с учётом сдвигов.</summary>
    public double Top { get; }

    public double Bottom { get; }

    public bool HasData => All.Any(source => source.Data.Depths.Length > 0);

    /// <summary>Шаг глубины основного файла — для точности подписи курсора.</summary>
    public double Step
    {
        get
        {
            double[] depths = Primary.Data.Depths;
            return depths.Length > 1 ? Math.Abs(depths[1] - depths[0]) : 0.1;
        }
    }

    /// <summary>Источник по ключу кривой; <c>null</c> — свой файл (у совместного планшета такого нет).</summary>
    public TabletSource? Find(string? key) => key is null ? Own : byKey.GetValueOrDefault(key);

    /// <summary>Кривая из своего файла (ключ <c>null</c> или ключ своего файла).</summary>
    public bool IsOwn(string? key) => Own is not null && (key is null || key == Own.Key);

    public TabletSeries? Series(CurveStyle style) => Series(style.Source, style.Mnemonic);

    public TabletSeries? Series(string? key, string mnemonic)
    {
        TabletSource? source = Find(key);
        if (source is null)
        {
            return null;
        }

        if (series.TryGetValue((source.Key, mnemonic), out TabletSeries? cached))
        {
            return cached;
        }

        double[]? values = source.Data.Values(mnemonic);
        TabletSeries? result = values is null ? null : new TabletSeries(source, Depths(source), values, source.Data.Unit(mnemonic));
        series[(source.Key, mnemonic)] = result;
        return result;
    }

    /// <summary>
    /// Подпись файла у кривой: на планшете файла — только у кривых других
    /// файлов, на совместном — у всех. <c>null</c> — подпись не нужна.
    /// </summary>
    public string? Label(string? key) => IsOwn(key) ? null : Find(key)?.Name;

    /// <summary>Почему кривой нет на планшете — для шапки трека.</summary>
    public string MissingReason(string? key) => Find(key) is null ? "файл не открыт" : "нет в файле";

    private double[] Depths(TabletSource source)
    {
        if (source.DepthShift == 0)
        {
            return source.Data.Depths;
        }

        if (!shiftedDepths.TryGetValue(source.Key, out double[]? depths))
        {
            depths = source.Data.Depths.Select(depth => depth + source.DepthShift).ToArray();
            shiftedDepths[source.Key] = depths;
        }

        return depths;
    }
}
