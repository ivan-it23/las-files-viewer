namespace AMK.LasViewer.Charting;

/// <summary>Оформление кривой на треке. Цвет — ARGB.</summary>
public sealed record CurveStyle(string Mnemonic, uint Color, double Thickness = 1.2);

public sealed class TrackDefinition
{
    public required TrackScale Scale { get; set; }

    public List<CurveStyle> Curves { get; init; } = [];

    public double WidthDip { get; set; } = TabletTemplate.DefaultTrackWidth;
}

/// <summary>Набор треков планшета. Трек глубины всегда первый и в список не входит.</summary>
public sealed class TabletTemplate
{
    public const double DefaultTrackWidth = 190;

    public List<TrackDefinition> Tracks { get; init; } = [];

    public double DepthTrackWidth { get; set; } = 72;

    public double TotalWidth => DepthTrackWidth + Tracks.Sum(track => track.WidthDip);
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
