using AMK.LasViewer.Core;

namespace AMK.LasViewer.Charting;

/// <summary>
/// Планшет по умолчанию для только что открытого файла.
/// </summary>
/// <remarks>
/// Кривые группируются по смыслу единицы измерения в порядке файла и
/// раскладываются по трекам не больше чем по <see cref="MaxCurvesPerTrack"/>.
/// УЭС — логарифмическая шкала по целым декадам, остальное — линейная шкала с
/// «круглыми» границами. Границы берутся по 1-му и 99-му процентилям, чтобы
/// единичный выброс не сжал все кривые трека в линию.
/// </remarks>
public static class DefaultTemplateBuilder
{
    public const int MaxCurvesPerTrack = 4;

    public const string AutomaticName = "Автоматический (по единицам)";

    private const string ResistivityGroup = "\u0001resistivity";
    private const int MaxSamplesForRange = 200_000;

    public static TabletTemplate Build(LasDocument document)
    {
        List<(string Key, List<LasCurve> Curves)> groups = [];
        foreach (LasCurve curve in document.DataCurves)
        {
            string key = GroupKey(curve);
            (string Key, List<LasCurve> Curves) group = groups.FirstOrDefault(g => g.Key == key);
            if (group.Curves is null)
            {
                group = (key, []);
                groups.Add(group);
            }

            group.Curves.Add(curve);
        }

        TabletTemplate template = new() { Name = AutomaticName };
        foreach ((string key, List<LasCurve> curves) in groups)
        {
            TrackScale scale = key == ResistivityGroup ? ResistivityScale(curves) : LinearScale(curves);
            foreach (LasCurve[] chunk in curves.Chunk(MaxCurvesPerTrack))
            {
                template.Tracks.Add(new TrackDefinition
                {
                    Scale = scale,
                    Curves = chunk
                        .Select((curve, position) => new CurveStyle { Mnemonic = curve.Mnemonic, Color = CurvePalette.ForPosition(position) })
                        .ToList()
                });
            }
        }

        return template;
    }

    public static bool IsResistivity(LasCurve curve)
    {
        string unit = NormalizeUnit(curve.Unit);
        if (unit is "омм" or "ohmm" or "ом" or "ohm")
        {
            return true;
        }

        string description = curve.Description.ToLowerInvariant();
        return description.Contains("уэс", StringComparison.Ordinal)
            || description.Contains("сопротивлен", StringComparison.Ordinal)
            || description.Contains("resistivity", StringComparison.Ordinal);
    }

    private static string GroupKey(LasCurve curve)
    {
        return IsResistivity(curve) ? ResistivityGroup : NormalizeUnit(curve.Unit);
    }

    private static string NormalizeUnit(string unit)
    {
        return new string(unit
            .ToLowerInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch is not '.' and not '·' and not '*' and not '-' and not '_')
            .ToArray());
    }

    private static TrackScale ResistivityScale(IReadOnlyList<LasCurve> curves)
    {
        (double low, double high)? range = Percentiles(curves, positiveOnly: true);
        if (range is null)
        {
            return TrackScale.Logarithmic(0.1, 1000);
        }

        int lowDecade = (int)Math.Floor(Math.Log10(range.Value.low) + 1e-9);
        int highDecade = (int)Math.Ceiling(Math.Log10(range.Value.high) - 1e-9);
        // Меньше двух декад на логарифмической шкале читается плохо.
        while (highDecade - lowDecade < 2)
        {
            highDecade++;
        }

        return TrackScale.Logarithmic(Math.Pow(10, lowDecade), Math.Pow(10, highDecade));
    }

    private static TrackScale LinearScale(IReadOnlyList<LasCurve> curves)
    {
        (double low, double high)? range = Percentiles(curves, positiveOnly: false);
        if (range is null)
        {
            return TrackScale.Linear(0, 1);
        }

        (double left, double right) = NiceRange(range.Value.low, range.Value.high);
        return TrackScale.Linear(left, right);
    }

    /// <summary>Границы из десяти «круглых» делений (1, 2, 2,5, 5 × 10ⁿ), охватывающие диапазон.</summary>
    public static (double Left, double Right) NiceRange(double low, double high)
    {
        if (high - low < 1e-12 * Math.Max(1, Math.Abs(high)))
        {
            double pad = Math.Abs(low) > 0 ? Math.Abs(low) * 0.1 : 1;
            low -= pad;
            high += pad;
        }

        double step = NiceStep((high - low) / 10);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            double left = Math.Floor(low / step) * step;
            double right = left + step * 10;
            if (right >= high - step * 1e-9)
            {
                return (Clean(left), Clean(right));
            }

            step = NiceStep(step * 1.01);
        }

        return (low, high);
    }

    private static double NiceStep(double rough)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        foreach (double multiplier in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (multiplier * magnitude >= rough * (1 - 1e-9))
            {
                return multiplier * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private static double Clean(double value) => Math.Round(value, 10);

    private static (double low, double high)? Percentiles(IReadOnlyList<LasCurve> curves, bool positiveOnly)
    {
        long total = curves.Sum(curve => (long)curve.Values.Length);
        int stride = (int)Math.Max(1, total / MaxSamplesForRange);

        List<double> sample = [];
        foreach (LasCurve curve in curves)
        {
            for (int index = 0; index < curve.Values.Length; index += stride)
            {
                double value = curve.Values[index];
                if (!double.IsNaN(value) && (!positiveOnly || value > 0))
                {
                    sample.Add(value);
                }
            }
        }

        if (sample.Count == 0)
        {
            return null;
        }

        sample.Sort();
        double low = sample[(int)Math.Floor((sample.Count - 1) * 0.01)];
        double high = sample[(int)Math.Ceiling((sample.Count - 1) * 0.99)];
        return (low, high);
    }
}
