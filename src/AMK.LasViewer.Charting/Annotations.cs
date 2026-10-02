namespace AMK.LasViewer.Charting;

/// <summary>Маркер на глубине: кровля пласта, отметка, комментарий.</summary>
public sealed class DepthMarker
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public double Depth { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public uint Color { get; set; } = 0xFFB00020;
}

/// <summary>Интервал глубин: пласт, участок для анализа, брак записи.</summary>
public sealed class DepthInterval
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public double Top { get; set; }

    public double Bottom { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public uint Color { get; set; } = 0xFFE6A100;
}

/// <summary>Пометки одного файла. Хранятся в файле проекта, а не в LAS.</summary>
public sealed class DocumentAnnotations
{
    public List<DepthMarker> Markers { get; init; } = [];

    public List<DepthInterval> Intervals { get; init; } = [];

    public bool IsEmpty => Markers.Count == 0 && Intervals.Count == 0;

    /// <summary>Маркер в пределах <paramref name="tolerance"/> от глубины, ближайший.</summary>
    public DepthMarker? MarkerNear(double depth, double tolerance)
    {
        return Markers
            .Where(marker => Math.Abs(marker.Depth - depth) <= tolerance)
            .OrderBy(marker => Math.Abs(marker.Depth - depth))
            .FirstOrDefault();
    }

    /// <summary>Самый узкий интервал, содержащий глубину.</summary>
    public DepthInterval? IntervalAt(double depth)
    {
        return Intervals
            .Where(interval => depth >= Math.Min(interval.Top, interval.Bottom) && depth <= Math.Max(interval.Top, interval.Bottom))
            .OrderBy(interval => Math.Abs(interval.Bottom - interval.Top))
            .FirstOrDefault();
    }
}
