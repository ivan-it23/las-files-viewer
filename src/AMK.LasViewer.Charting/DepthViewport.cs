namespace AMK.LasViewer.Charting;

/// <summary>
/// Видимый участок по глубине: верхняя граница и масштаб в эффективных
/// пикселях (DIP) на единицу индекса.
/// </summary>
public readonly record struct DepthViewport(double Top, double PixelsPerUnit, double HeightDip)
{
    /// <summary>Эффективных пикселей WinUI в миллиметре: 96 DIP на дюйм.</summary>
    public const double DipPerMillimetre = 96.0 / 25.4;

    public double Span => HeightDip / PixelsPerUnit;

    public double Bottom => Top + Span;

    public double ToY(double depth) => (depth - Top) * PixelsPerUnit;

    public double ToDepth(double y) => Top + y / PixelsPerUnit;

    /// <summary>
    /// DIP на единицу индекса для масштаба 1:<paramref name="ratio"/>. Масштаб
    /// 1:500 — это 2 мм экрана на метр глубины. Для индекса в футах в единице
    /// 304,8 мм.
    /// </summary>
    public static double PixelsPerUnitForRatio(double ratio, string indexUnit)
    {
        double millimetresPerUnit = IsFeet(indexUnit) ? 304.8 : 1000.0;
        return millimetresPerUnit / ratio * DipPerMillimetre;
    }

    public static double RatioForPixelsPerUnit(double pixelsPerUnit, string indexUnit)
    {
        double millimetresPerUnit = IsFeet(indexUnit) ? 304.8 : 1000.0;
        return millimetresPerUnit * DipPerMillimetre / pixelsPerUnit;
    }

    private static bool IsFeet(string unit)
    {
        string normalized = unit.Trim().ToUpperInvariant();
        return normalized is "FT" or "F" or "FEET" or "ФТ";
    }
}

/// <summary>Горизонтальная линия сетки глубин.</summary>
public readonly record struct DepthTick(double Depth, bool IsMajor);

public static class DepthTicks
{
    private static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000];

    /// <summary>
    /// Основной шаг — наименьший, при котором подписи отстоят хотя бы на
    /// <paramref name="minimumLabelSpacingDip"/>; промежуточные линии — пятые
    /// (или половинные для шага с двойкой), если между ними не меньше 6 DIP.
    /// </summary>
    public static IReadOnlyList<DepthTick> Generate(DepthViewport viewport, double minimumLabelSpacingDip = 50)
    {
        double major = MajorStep(viewport, minimumLabelSpacingDip);
        double minor = IsTwoStep(major) ? major / 2 : major / 5;
        bool withMinor = minor * viewport.PixelsPerUnit >= 6;
        double step = withMinor ? minor : major;

        List<DepthTick> ticks = [];
        double first = Math.Floor(viewport.Top / step) * step;
        for (double depth = first; depth <= viewport.Bottom + step * 0.5; depth += step)
        {
            // Накопление ошибки сложения сдвигает метки; глубина пересчитывается
            // от номера деления.
            double rounded = Math.Round(depth / step) * step;
            if (rounded < viewport.Top - step * 0.5)
            {
                continue;
            }

            bool isMajor = Math.Abs(rounded / major - Math.Round(rounded / major)) < 1e-6;
            ticks.Add(new DepthTick(rounded, isMajor));
        }

        return ticks;
    }

    public static double MajorStep(DepthViewport viewport, double minimumLabelSpacingDip = 50)
    {
        foreach (double step in Steps)
        {
            if (step * viewport.PixelsPerUnit >= minimumLabelSpacingDip)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    private static bool IsTwoStep(double step)
    {
        double mantissa = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
        return Math.Abs(mantissa - 2) < 1e-9;
    }
}
