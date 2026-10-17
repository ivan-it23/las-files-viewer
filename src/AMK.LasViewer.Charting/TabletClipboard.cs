namespace AMK.LasViewer.Charting;

/// <summary>
/// Буфер обмена планшетов: скопированные кривые и треки, которые можно
/// вставить на планшет другого файла или на совместный планшет.
/// </summary>
/// <remarks>
/// Свой у приложения, не системный: в нём не данные, а ссылки на кривые
/// открытых файлов с оформлением. Ссылки на файлы в буфере всегда явные
/// (ключ файла, а не «свой файл»): на другом планшете «свой» — другой файл.
/// Хранит до <see cref="Capacity"/> элементов, новые — первыми.
/// </remarks>
public sealed class TabletClipboard
{
    public const int Capacity = 20;

    private readonly List<ClipboardEntry> entries = [];

    /// <summary>Содержимое буфера изменилось.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ClipboardEntry> Entries => entries;

    public IEnumerable<CopiedCurve> Curves => entries.OfType<CopiedCurve>();

    public IEnumerable<CopiedTrack> Tracks => entries.OfType<CopiedTrack>();

    public int Count => entries.Count;

    /// <summary>
    /// Кладёт в буфер. Кривая, которая там уже есть (тот же файл и
    /// мнемоника), заменяется — с новым оформлением и на первом месте.
    /// </summary>
    public void Add(ClipboardEntry entry)
    {
        if (entry is CopiedCurve curve)
        {
            entries.RemoveAll(existing => existing is CopiedCurve other && TemplateEditing.SameCurve(other.Curve, curve.Curve));
        }

        entries.Insert(0, entry);
        if (entries.Count > Capacity)
        {
            entries.RemoveRange(Capacity, entries.Count - Capacity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (entries.Count > 0)
        {
            entries.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Файл закрыт: кривые этого файла из буфера убираются, из треков —
    /// вычёркиваются (трек без кривых убирается целиком).
    /// </summary>
    public void RemoveFile(string fileKey)
    {
        bool changed = false;
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            switch (entries[index])
            {
                case CopiedCurve curve when curve.Curve.Source == fileKey:
                    entries.RemoveAt(index);
                    changed = true;
                    break;
                case CopiedTrack track when track.Files.Contains(fileKey):
                    TrackDefinition rest = track.Track.Clone();
                    rest.Curves.RemoveAll(curve => curve.Source == fileKey);
                    rest.Fills.RemoveAll(fill => fill.Source == fileKey);
                    if (rest.Curves.Count == 0)
                    {
                        entries.RemoveAt(index);
                    }
                    else
                    {
                        entries[index] = track with { Track = rest };
                    }

                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Элемент буфера обмена планшетов.</summary>
public abstract record ClipboardEntry
{
    /// <summary>Подпись в меню «Вставить».</summary>
    public abstract string Title { get; }

    /// <summary>Файлы, на кривые которых ссылается элемент.</summary>
    public abstract IReadOnlySet<string> Files { get; }
}

/// <summary>
/// Скопированная кривая: ссылка на файл (<see cref="CurveStyle.Source"/> —
/// всегда ключ файла) и оформление со шкалой, с которой она была на треке.
/// </summary>
public sealed record CopiedCurve(CurveStyle Curve, string FileName) : ClipboardEntry
{
    public override string Title => FileName.Length > 0 ? $"{Curve.Mnemonic} · {FileName}" : Curve.Mnemonic;

    public override IReadOnlySet<string> Files => new HashSet<string> { Curve.Source ?? string.Empty };
}

/// <summary>
/// Скопированный трек: кривые, заливки, шкала и ширина; ссылки на файлы у
/// всех кривых и заливок — явные.
/// </summary>
public sealed record CopiedTrack(TrackDefinition Track, string FileNames) : ClipboardEntry
{
    public override string Title
    {
        get
        {
            string curves = string.Join(", ", Track.Curves.Take(4).Select(curve => curve.Mnemonic))
                            + (Track.Curves.Count > 4 ? $" и ещё {Track.Curves.Count - 4}" : string.Empty);
            return $"Трек: {curves}" + (FileNames.Length > 0 ? $" · {FileNames}" : string.Empty);
        }
    }

    public override IReadOnlySet<string> Files =>
        Track.Curves.Select(curve => curve.Source).Concat(Track.Fills.Select(fill => fill.Source)).OfType<string>().ToHashSet();
}
