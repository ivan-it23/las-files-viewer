namespace AMK.LasViewer.Charting;

/// <summary>
/// Правки шаблона, общие для экрана и проверок: куда ложится кривая другого
/// файла и как переносятся ссылки на файлы.
/// </summary>
public static class TemplateEditing
{
    /// <summary>
    /// Кладёт кривую на трек <paramref name="trackIndex"/>.
    /// </summary>
    /// <remarks>
    /// Если на треке кривые одного файла, а кривая — из другого, трек не
    /// меняется: справа от него появляется новый трек с копиями его кривых и
    /// новой кривой. Так кривые разных методов сравниваются на одном треке, а
    /// исходный трек остаётся как был. На пустой трек и на трек, где уже
    /// смешаны файлы, кривая добавляется как есть.
    /// <para>
    /// Кривая на треке чужого файла получает свою шкалу (<paramref name="suggestScale"/>):
    /// шкала трека подобрана под кривые другого метода.
    /// </para>
    /// </remarks>
    /// <returns>Номер трека, на который легла кривая, или −1, если она там уже есть.</returns>
    public static int PlaceCurve(TabletTemplate template, int trackIndex, CurveStyle curve, Func<TrackScale> suggestScale)
    {
        if (trackIndex < 0 || trackIndex >= template.Tracks.Count)
        {
            return -1;
        }

        TrackDefinition track = template.Tracks[trackIndex];
        if (track.Curves.Any(existing => SameCurve(existing, curve)))
        {
            return -1;
        }

        if (track.Curves.Count == 0)
        {
            track.Scale = curve.Scale ?? suggestScale();
            curve.Scale = null;
            curve.Color = CurvePalette.ForPosition(0);
            track.Curves.Add(curve);
            return trackIndex;
        }

        bool otherFile = track.Curves.Any(existing => existing.Source != curve.Source);
        if (otherFile)
        {
            curve.Scale ??= suggestScale();
        }

        string?[] sources = track.Curves.Select(existing => existing.Source).Distinct().ToArray();
        if (sources.Length == 1 && sources[0] != curve.Source)
        {
            // Трек сравнения: копии кривых трека и новая кривая. Заливки не
            // копируются — трек нужен, чтобы видеть кривые разных методов вместе.
            TrackDefinition comparison = new()
            {
                Scale = track.Scale,
                WidthDip = track.WidthDip,
                Curves = track.Curves.Select(existing => existing.Clone()).ToList()
            };
            curve.Color = FreeColor(comparison);
            comparison.Curves.Add(curve);
            template.Tracks.Insert(trackIndex + 1, comparison);
            return trackIndex + 1;
        }

        curve.Color = FreeColor(track);
        track.Curves.Add(curve);
        return trackIndex;
    }

    public static bool SameCurve(CurveStyle first, CurveStyle second) =>
        first.Mnemonic == second.Mnemonic && first.Source == second.Source;

    // ------------------------------------------------------------------
    // Буфер обмена: копирование и вставка кривых и треков
    // ------------------------------------------------------------------

    /// <summary>
    /// Кривая для буфера обмена: копия с явной ссылкой на файл и со шкалой,
    /// с которой она нарисована на треке, — чтобы на другом планшете она
    /// выглядела так же.
    /// </summary>
    /// <param name="ownKey">Ключ файла планшета, с которого копируют (для «своих» кривых).</param>
    public static CurveStyle CopyCurve(CurveStyle curve, TrackDefinition track, string? ownKey)
    {
        CurveStyle copy = curve.Clone();
        copy.Source ??= ownKey;
        copy.Scale = curve.EffectiveScale(track);
        return copy;
    }

    /// <summary>Трек для буфера обмена: копия, где у всех кривых и заливок ссылка на файл явная.</summary>
    public static TrackDefinition CopyTrack(TrackDefinition track, string? ownKey)
    {
        TrackDefinition copy = track.Clone();
        copy.Curves.ForEach(curve => curve.Source ??= ownKey);
        copy.Fills.ForEach(fill => fill.Source ??= ownKey);
        return copy;
    }

    /// <summary>
    /// Кладёт кривую из буфера поверх кривых трека — без трека сравнения.
    /// На пустом треке её шкала становится шкалой трека; на занятом кривая
    /// сохраняет свою шкалу (если она не совпадает со шкалой трека) и
    /// получает свободный цвет, если её цвет на треке уже занят.
    /// </summary>
    /// <param name="curve">Копия из буфера; ссылка на файл — уже для этого планшета (<c>null</c> — свой файл).</param>
    /// <returns>Номер трека или −1, если такая кривая на треке уже есть.</returns>
    public static int OverlayCurve(TabletTemplate template, int trackIndex, CurveStyle curve)
    {
        if (trackIndex < 0 || trackIndex >= template.Tracks.Count)
        {
            return -1;
        }

        TrackDefinition track = template.Tracks[trackIndex];
        if (track.Curves.Any(existing => SameCurve(existing, curve)))
        {
            return -1;
        }

        if (track.Curves.Count == 0)
        {
            track.Scale = curve.Scale ?? track.Scale;
            curve.Scale = null;
        }
        else if (curve.Scale == track.Scale)
        {
            curve.Scale = null;
        }

        if (track.Curves.Any(existing => existing.Color == curve.Color))
        {
            curve.Color = FreeColor(track);
        }

        track.Curves.Add(curve);
        return trackIndex;
    }

    /// <summary>Новый трек на месте <paramref name="position"/> с одной кривой из буфера (её шкала — шкала трека).</summary>
    public static int CurveOnNewTrack(TabletTemplate template, int position, CurveStyle curve)
    {
        position = Math.Clamp(position, 0, template.Tracks.Count);
        template.Tracks.Insert(position, new TrackDefinition { Scale = curve.Scale ?? TrackScale.Linear(0, 100) });
        curve.Scale = null;
        template.Tracks[position].Curves.Add(curve);
        return position;
    }

    /// <summary>
    /// Трек из буфера на месте <paramref name="position"/>. Ссылки на файл
    /// <paramref name="ownKey"/> становятся «своими» (<c>null</c>), остальные
    /// остаются явными.
    /// </summary>
    public static int PasteTrack(TabletTemplate template, int position, TrackDefinition copied, string? ownKey)
    {
        TrackDefinition track = copied.Clone();
        foreach (CurveStyle curve in track.Curves.Where(curve => ownKey is not null && curve.Source == ownKey))
        {
            curve.Source = null;
        }

        foreach (FillDefinition fill in track.Fills.Where(fill => ownKey is not null && fill.Source == ownKey))
        {
            fill.Source = null;
        }

        position = Math.Clamp(position, 0, template.Tracks.Count);
        template.Tracks.Insert(position, track);
        return position;
    }

    /// <summary>
    /// Копия шаблона, в которой ссылки на файлы заменены по <paramref name="map"/>.
    /// Кривые и заливки, для которых <paramref name="map"/> вернул <c>null</c>, убираются:
    /// их файла больше нет. Ссылка <c>null</c> (свой файл) не меняется.
    /// </summary>
    public static TabletTemplate RemapSources(TabletTemplate template, Func<string, string?> map)
    {
        TabletTemplate copy = template.Clone();
        foreach (TrackDefinition track in copy.Tracks)
        {
            foreach (CurveStyle curve in track.Curves.ToList())
            {
                if (curve.Source is { } source)
                {
                    curve.Source = map(source);
                    if (curve.Source is null)
                    {
                        track.Curves.Remove(curve);
                    }
                }
            }

            foreach (FillDefinition fill in track.Fills.ToList())
            {
                if (fill.Source is { } source)
                {
                    fill.Source = map(source);
                    if (fill.Source is null)
                    {
                        track.Fills.Remove(fill);
                    }
                }
            }
        }

        return copy;
    }

    /// <summary>
    /// Копия шаблона только с кривыми и заливками своего файла — для
    /// библиотеки шаблонов: шаблон применяется к другим файлам, и ссылка на
    /// конкретный файл в нём смысла не имеет. Трек, где были только чужие
    /// кривые, убирается.
    /// </summary>
    public static TabletTemplate WithoutOtherFiles(TabletTemplate template)
    {
        TabletTemplate copy = template.Clone();
        for (int index = copy.Tracks.Count - 1; index >= 0; index--)
        {
            TrackDefinition track = copy.Tracks[index];
            bool hadCurves = track.Curves.Count > 0;
            track.Curves.RemoveAll(curve => curve.Source is not null);
            track.Fills.RemoveAll(fill => fill.Source is not null);
            if (hadCurves && track.Curves.Count == 0)
            {
                copy.Tracks.RemoveAt(index);
            }
        }

        return copy;
    }

    /// <summary>Цвет палитры, которого ещё нет на треке.</summary>
    private static uint FreeColor(TrackDefinition track)
    {
        for (int position = 0; position < CurvePalette.Colors.Length; position++)
        {
            uint color = CurvePalette.ForPosition(position);
            if (track.Curves.All(curve => curve.Color != color))
            {
                return color;
            }
        }

        return CurvePalette.ForPosition(track.Curves.Count);
    }
}
