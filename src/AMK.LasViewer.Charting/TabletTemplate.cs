namespace AMK.LasViewer.Charting;

public enum LineDash
{
    Solid,
    Dash,
    Dot
}

/// <summary>Что делать со значением за пределами шкалы.</summary>
public enum WrapMode
{
    /// <summary>Прижать к краю трека.</summary>
    Clamp,

    /// <summary>
    /// Перенести на следующий «оборот» шкалы и нарисовать пунктиром — как
    /// резервная шкала на бумажном планшете. Не больше двух оборотов в каждую
    /// сторону, дальше значение прижимается к краю.
    /// </summary>
    Wrap
}

/// <summary>Оформление кривой на треке. Цвет — ARGB.</summary>
public sealed class CurveStyle
{
    public required string Mnemonic { get; set; }

    /// <summary>
    /// Файл, из которого взята кривая (ключ источника — <see cref="TabletSource.Key"/>);
    /// <c>null</c> — файл самого планшета. На совместном планшете источник
    /// указан у каждой кривой.
    /// </summary>
    public string? Source { get; set; }

    public uint Color { get; set; } = CurvePalette.Colors[0];

    public double Thickness { get; set; } = 1.2;

    public LineDash Dash { get; set; } = LineDash.Solid;

    /// <summary>Собственная шкала кривой; <c>null</c> — шкала трека.</summary>
    public TrackScale? Scale { get; set; }

    public WrapMode Wrap { get; set; } = WrapMode.Clamp;

    public TrackScale EffectiveScale(TrackDefinition track) => Scale ?? track.Scale;

    public CurveStyle Clone() => (CurveStyle)MemberwiseClone();
}

public enum FillKind
{
    /// <summary>От левого края трека до кривой.</summary>
    ToLeftEdge,

    /// <summary>От кривой до правого края трека.</summary>
    ToRightEdge,

    /// <summary>Между кривой и значением её шкалы (граничное значение).</summary>
    ToValue,

    /// <summary>Между двумя кривыми трека.</summary>
    BetweenCurves
}

/// <summary>
/// Заливка на треке. Для <see cref="FillKind.ToValue"/> и
/// <see cref="FillKind.BetweenCurves"/> стороны различаются: где кривая правее
/// границы — <see cref="Color"/>, где левее — <see cref="OppositeColor"/>
/// (если не задан, эта сторона не заливается). Так подсвечиваются пересечения.
/// </summary>
public sealed class FillDefinition
{
    public FillKind Kind { get; set; }

    public required string Curve { get; set; }

    public string? OtherCurve { get; set; }

    /// <summary>
    /// Файл обеих кривых заливки (<see cref="CurveStyle.Source"/>). Заливка
    /// между кривыми разных файлов не строится: для неё нужен пересчёт на
    /// общую сетку глубин.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Граничное значение для <see cref="FillKind.ToValue"/> в шкале кривой.</summary>
    public double Value { get; set; }

    public uint Color { get; set; } = 0x553A7BD5;

    public uint? OppositeColor { get; set; }

    public FillDefinition Clone() => (FillDefinition)MemberwiseClone();
}

public sealed class TrackDefinition
{
    /// <summary>Шкала трека: по ней строится сетка и рисуются кривые без собственной шкалы.</summary>
    public required TrackScale Scale { get; set; }

    public List<CurveStyle> Curves { get; init; } = [];

    public List<FillDefinition> Fills { get; init; } = [];

    /// <summary>Ширина трека в DIP. На экране треки делят ширину окна пропорционально ей.</summary>
    public double WidthDip { get; set; } = TabletTemplate.DefaultTrackWidth;

    public TrackDefinition Clone() => new()
    {
        Scale = Scale,
        Curves = Curves.Select(curve => curve.Clone()).ToList(),
        Fills = Fills.Select(fill => fill.Clone()).ToList(),
        WidthDip = WidthDip
    };
}

/// <summary>Набор треков планшета. Трек глубины всегда первый и в список не входит.</summary>
public sealed class TabletTemplate
{
    public const double DefaultTrackWidth = 190;

    public string Name { get; set; } = string.Empty;

    public List<TrackDefinition> Tracks { get; init; } = [];

    public double DepthTrackWidth { get; set; } = 72;

    public double TotalWidth => DepthTrackWidth + Tracks.Sum(track => track.WidthDip);

    public IEnumerable<string> Mnemonics => Tracks.SelectMany(track => track.Curves).Select(curve => curve.Mnemonic);

    public TabletTemplate Clone() => new()
    {
        Name = Name,
        DepthTrackWidth = DepthTrackWidth,
        Tracks = Tracks.Select(track => track.Clone()).ToList()
    };
}

public static class CurvePalette
{
    /// <summary>
    /// Цвета по порядку кривой в треке. Порядок одинаков во всех треках, поэтому
    /// у кривых одного зонда (F1 и RO1) один цвет.
    /// </summary>
    public static readonly uint[] Colors =
    [
        0xFF1F5FBF, // синий
        0xFFD62728, // красный
        0xFF2CA02C, // зелёный
        0xFF8E44AD, // фиолетовый
        0xFFE67E22, // оранжевый
        0xFF7F5539  // коричневый
    ];

    public static uint ForPosition(int position) => Colors[position % Colors.Length];
}
