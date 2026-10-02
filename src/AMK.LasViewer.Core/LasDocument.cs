namespace AMK.LasViewer.Core;

/// <summary>
/// Открытый LAS-файл в том виде, в каком его прочитал движок. Приложение
/// держит копию для отображения; источник истины — движок.
/// </summary>
public sealed class LasDocument
{
    public required string FileId { get; init; }

    public required string Path { get; init; }

    public required string Name { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Имя кодировки для показа пользователю, например «Windows-1251».</summary>
    public required string EncodingDisplay { get; init; }

    /// <summary>bom, utf-8, ascii или heuristic — как определена кодировка.</summary>
    public required string EncodingMethod { get; init; }

    public required string Version { get; init; }

    public bool Wrap { get; init; }

    public required string Delimiter { get; init; }

    public double? NullValue { get; init; }

    public int RowCount { get; init; }

    public required LasIndexInfo Index { get; init; }

    public required IReadOnlyList<LasSection> Sections { get; init; }

    /// <summary>Все кривые в порядке файла; первая — индекс (глубина или время).</summary>
    public required IReadOnlyList<LasCurve> Curves { get; init; }

    public required IReadOnlyList<LasIssue> Issues { get; init; }

    public LasCurve IndexCurve => Curves[0];

    public IEnumerable<LasCurve> DataCurves => Curves.Skip(1);

    public LasSection? Section(LasSectionKind kind) => Sections.FirstOrDefault(section => section.Kind == kind);

    public bool HasWarnings => Issues.Any(issue => issue.Level != LasIssueLevel.Info);
}

public sealed class LasCurve
{
    public required string Mnemonic { get; init; }

    /// <summary>Мнемоника в файле; отличается от <see cref="Mnemonic"/>, если повтор переименован.</summary>
    public required string OriginalMnemonic { get; init; }

    public required string Unit { get; init; }

    public required string ApiCode { get; init; }

    public required string Description { get; init; }

    public required CurveStatistics Statistics { get; init; }

    /// <summary>Значения по строкам файла; пропуски (NULL, нечисловые) — <see cref="double.NaN"/>.</summary>
    public required double[] Values { get; init; }

    public string DisplayName => Unit.Length == 0 ? Mnemonic : $"{Mnemonic}, {Unit}";
}

public sealed record CurveStatistics(int Count, int Nulls, double? Min, double? Max, double? Mean);

public sealed record LasHeaderItem(string Mnemonic, string Unit, string Value, string Description, int Line);

public enum LasSectionKind
{
    Version,
    Well,
    Curve,
    Parameter,
    Other,
    Data,
    Unknown
}

public sealed record LasSection(
    LasSectionKind Kind,
    string Title,
    int Line,
    IReadOnlyList<LasHeaderItem> Items,
    string Text);

public enum LasIssueLevel
{
    Info,
    Warning,
    Error
}

public sealed record LasIssue(LasIssueLevel Level, string Code, string Message, int? Line);

public enum IndexDirection
{
    Increasing,
    Decreasing,
    Unordered,
    Unknown
}

public sealed record LasIndexInfo(
    string Mnemonic,
    string Unit,
    double? Start,
    double? Stop,
    double? Step,
    IndexDirection Direction,
    double? HeaderStart,
    double? HeaderStop,
    double? HeaderStep);
