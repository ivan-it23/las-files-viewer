namespace AMK.LasViewer.Charting;

public enum ScaleKind
{
    Linear,
    Logarithmic
}

/// <summary>Вертикальная линия сетки трека: доля ширины от левого края.</summary>
public readonly record struct GridLine(double Fraction, bool IsMajor, double Value);

/// <summary>
/// Горизонтальная шкала трека: значение → доля ширины трека (0 — левый край,
/// 1 — правый). Левая граница может быть больше правой — шкала тогда обращена.
/// </summary>
public sealed record TrackScale(ScaleKind Kind, double Left, double Right)
{
    public static TrackScale Linear(double left, double right) => new(ScaleKind.Linear, left, right);

    public static TrackScale Logarithmic(double left, double right)
    {
        if (left <= 0 || right <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left), "Границы логарифмической шкалы должны быть положительными.");
        }

        return new TrackScale(ScaleKind.Logarithmic, left, right);
    }

    /// <summary>
    /// Доля ширины для значения. Значения за пределами шкалы дают долю вне
    /// [0; 1]; неположительное значение на логарифмической шкале — NaN.
    /// </summary>
    public double ToFraction(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (Kind == ScaleKind.Linear)
        {
            return (value - Left) / (Right - Left);
        }

        if (value <= 0)
        {
            return double.NaN;
        }

        double logLeft = Math.Log10(Left);
        return (Math.Log10(value) - logLeft) / (Math.Log10(Right) - logLeft);
    }

    /// <summary>
    /// Доля ширины с переносом за шкалу: значение правее шкалы попадает на
    /// следующий «оборот» (<paramref name="band"/> = 1, 2), левее — на
    /// предыдущий (−1, −2). Сама шкала [0; 1] — оборот 0, обе границы включены.
    /// Дальше <paramref name="maxBands"/> оборотов значение прижимается к краю.
    /// </summary>
    public double ToWrappedFraction(double value, out int band, int maxBands = 2)
    {
        double fraction = ToFraction(value);
        band = 0;
        if (double.IsNaN(fraction) || fraction is >= 0 and <= 1)
        {
            return fraction;
        }

        int raw = (int)Math.Floor(fraction);
        band = Math.Clamp(raw, -maxBands, maxBands);
        return raw == band ? fraction - band : (band > 0 ? 1 : 0);
    }

    public IReadOnlyList<GridLine> GridLines(double widthDip)
    {
        return Kind == ScaleKind.Linear ? LinearGrid() : LogarithmicGrid(widthDip);
    }

    /// <summary>Десять делений, каждое пятое — основное: привычная сетка каротажного планшета.</summary>
    private List<GridLine> LinearGrid()
    {
        List<GridLine> lines = new(11);
        for (int index = 0; index <= 10; index++)
        {
            double fraction = index / 10.0;
            lines.Add(new GridLine(fraction, index % 5 == 0, Left + (Right - Left) * fraction));
        }

        return lines;
    }

    /// <summary>Основные линии — на декадах; промежуточные 2…9 — если декада достаточно широка.</summary>
    private List<GridLine> LogarithmicGrid(double widthDip)
    {
        double low = Math.Min(Left, Right);
        double high = Math.Max(Left, Right);
        int firstDecade = (int)Math.Floor(Math.Log10(low) + 1e-9);
        int lastDecade = (int)Math.Ceiling(Math.Log10(high) - 1e-9);
        int decades = Math.Max(1, lastDecade - firstDecade);
        bool withMinor = widthDip / decades >= 40;

        List<GridLine> lines = [];
        for (int decade = firstDecade; decade <= lastDecade; decade++)
        {
            double baseValue = Math.Pow(10, decade);
            for (int multiplier = 1; multiplier <= 9; multiplier++)
            {
                if (multiplier > 1 && !withMinor)
                {
                    break;
                }

                double value = baseValue * multiplier;
                if (value < low * (1 - 1e-9) || value > high * (1 + 1e-9))
                {
                    continue;
                }

                lines.Add(new GridLine(ToFraction(value), multiplier == 1, value));
            }
        }

        return lines;
    }
}
