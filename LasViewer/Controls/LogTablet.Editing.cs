using System.Globalization;
using AMK.LasViewer.App.Dialogs;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>Шапка планшета и редактирование: треки, кривые, заливки, маркеры, интервалы.</summary>
public sealed partial class LogTablet
{
    /// <summary>
    /// Ключ свойства перетаскивания внутри приложения. Значение — строка
    /// «curve|файл|мнемоника» (из дерева проекта) или
    /// «move|планшет|трек|файл|мнемоника» (из шапки трека). Файл — идентификатор
    /// движка; мнемоника последней, в ней может встретиться «|».
    /// </summary>
    public const string DragPropertyKey = "lasviewer-drag";

    private static readonly SolidColorBrush HeaderBackground = new(Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));
    private static readonly SolidColorBrush HeaderBorderBrush = new(Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8));
    private static readonly SolidColorBrush MutedTextBrush = new(Color.FromArgb(0xFF, 0x66, 0x66, 0x66));
    private static readonly SolidColorBrush DropHighlight = new(Color.FromArgb(0xFF, 0xE3, 0xEE, 0xFB));

    private readonly List<Border> headerBorders = [];
    private readonly List<(TextBlock Text, CurveStyle Curve)> headerValueTexts = [];

    // ------------------------------------------------------------------
    // Шапка
    // ------------------------------------------------------------------

    private void BuildHeaders()
    {
        headerPanel.Children.Clear();
        headerBorders.Clear();
        headerValueTexts.Clear();

        StackPanel depthTitle = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4) };
        depthTitle.Children.Add(new TextBlock { Text = "Глубина", FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        depthTitle.Children.Add(new TextBlock
        {
            Text = data!.IndexUnit.Length > 0 ? data.IndexUnit : "—",
            FontSize = 11,
            Foreground = MutedTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Border depthHeader = HeaderBorder(depthTitle);
        depthHeader.ContextFlyout = DynamicMenu(menu =>
        {
            menu.Items.Add(Item("Вставить трек", () => InsertTrack(0)));
            AddPasteMenu(menu, null);
            AddDepthShiftItem(menu);
        });
        headerBorders.Add(depthHeader);
        headerPanel.Children.Add(depthHeader);

        for (int index = 0; index < template!.Tracks.Count; index++)
        {
            int trackIndex = index;
            TrackDefinition track = template.Tracks[index];
            StackPanel rows = new() { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(6, 4, 6, 4), Spacing = 2 };
            if (track.Curves.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "Пустой трек: перетащите сюда кривую из дерева проекта или с другого трека",
                    FontSize = 11,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // Совместный планшет: над треком одного файла — имя файла.
            if (TabletSceneBuilder.TrackCaption(data!, track) is { } caption)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = caption,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MutedTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            foreach (CurveStyle style in track.Curves)
            {
                rows.Children.Add(CurveHeader(trackIndex, track, style));
            }

            Border header = HeaderBorder(rows);
            header.ContextFlyout = DynamicMenu(menu => PopulateTrackMenu(menu, trackIndex));
            AttachTrackDropTarget(header, trackIndex);
            headerBorders.Add(header);
            headerPanel.Children.Add(header);
        }
    }

    private static Border HeaderBorder(UIElement child) => new()
    {
        Background = HeaderBackground,
        BorderBrush = HeaderBorderBrush,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Child = child
    };

    private UIElement CurveHeader(int trackIndex, TrackDefinition track, CurveStyle style)
    {
        SolidColorBrush brush = SceneRenderer.Brush(style.Color);
        TrackScale scale = style.EffectiveScale(track);
        TabletSeries? series = data!.Series(style);
        bool present = series is not null;

        Grid grid = new() { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new Line
        {
            X1 = 0,
            X2 = 16,
            Y1 = 1.5,
            Y2 = 1.5,
            Stroke = brush,
            StrokeThickness = 2.5,
            StrokeDashArray = SceneRenderer.DashArray(style.Dash),
            VerticalAlignment = VerticalAlignment.Center
        });

        TextBlock name = new()
        {
            Text = TabletSceneBuilder.CurveTitle(data, style, track) + (style.Wrap == WrapMode.Wrap ? " ↻" : string.Empty),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        TextBlock valueText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);
        headerValueTexts.Add((valueText, style));

        string unit = series?.Unit ?? data.MissingReason(style.Source);
        if (present && scale.Kind == ScaleKind.Logarithmic)
        {
            unit = unit.Length > 0 ? unit + ", лог." : "лог.";
        }

        Grid scaleRow = new();
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scaleRow.Children.Add(ScaleText(TabletSceneBuilder.FormatScale(scale.Left, CultureInfo.CurrentCulture), HorizontalAlignment.Left, 0));
        scaleRow.Children.Add(ScaleText(unit, HorizontalAlignment.Center, 1));
        scaleRow.Children.Add(ScaleText(TabletSceneBuilder.FormatScale(scale.Right, CultureInfo.CurrentCulture), HorizontalAlignment.Right, 2));
        Grid.SetRow(scaleRow, 1);
        Grid.SetColumnSpan(scaleRow, 3);
        grid.Children.Add(scaleRow);

        // Шкала подчёркнута цветом кривой — как подпись шкалы на бумажном планшете.
        Border row = new()
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
            Padding = new Thickness(0, 0, 0, 1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CanDrag = Dialogs is not null
        };
        string file = series?.Source.Name ?? data.Find(style.Source)?.Name ?? string.Empty;
        ToolTipService.SetToolTip(row, (file.Length > 0 ? $"Файл {file}. " : string.Empty)
            + "Двойной щелчок — свойства кривой; перетащите на другой трек или на соседний планшет; "
            + "правая кнопка — «Копировать кривую» для вставки на любой планшет");
        row.ContextFlyout = DynamicMenu(menu => PopulateCurveMenu(menu, trackIndex, style));
        row.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await EditCurveAsync(trackIndex, style);
        };
        // В перетаскивании — файл кривой, а не «свой»: на чужом планшете
        // «свой файл» означал бы другой файл.
        string sourceFile = style.Source ?? data.Own?.Key ?? string.Empty;
        row.DragStarting += (_, e) =>
        {
            e.Data.Properties[DragPropertyKey] = $"move|{documentKey}|{trackIndex}|{sourceFile}|{style.Mnemonic}";
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        };
        return row;
    }

    private static TextBlock ScaleText(string text, HorizontalAlignment alignment, int column)
    {
        TextBlock block = new() { Text = text, FontSize = 10, Foreground = MutedTextBrush, HorizontalAlignment = alignment };
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>Значения кривых на глубине курсора — у каждой по её собственной записи.</summary>
    private void UpdateHeaderValues(double? depth)
    {
        foreach ((TextBlock text, CurveStyle curve) in headerValueTexts)
        {
            TabletSeries? series = data?.Series(curve);
            text.Text = depth is { } at && series is not null ? FormatValue(series.ValueAt(at)) : string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Перетаскивание кривых
    // ------------------------------------------------------------------

    private void AttachTrackDropTarget(FrameworkElement target, int trackIndex)
    {
        target.AllowDrop = true;
        Brush? original = (target as Border)?.Background ?? (target as Panel)?.Background;

        target.DragOver += (_, e) =>
        {
            if (ParseDrag(e.DataView) is not { } drag || Dialogs is null || template is null || data is null)
            {
                return;
            }

            if (DropRefusal(drag) is { } refusal)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.Caption = refusal;
                return;
            }

            bool move = IsInternalMove(drag);
            e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = move
                ? $"Перенести {drag.Mnemonic} на трек {trackIndex + 1}"
                : WouldCreateComparisonTrack(trackIndex, CurveSourceFor(drag.File))
                    ? $"Новый трек: {drag.Mnemonic} и кривые трека {trackIndex + 1}"
                    : $"Добавить {drag.Mnemonic} на трек {trackIndex + 1}";
            SetBackground(target, DropHighlight);
        };
        target.DragLeave += (_, _) => SetBackground(target, original);
        target.Drop += async (_, e) =>
        {
            SetBackground(target, original);
            if (ParseDrag(e.DataView) is not { } drag || template is null || data is null || DropRefusal(drag) is not null)
            {
                return;
            }

            if (IsInternalMove(drag))
            {
                MoveCurve(drag.FromTrack!.Value, new CurveStyle { Mnemonic = drag.Mnemonic, Source = CurveSourceFor(drag.File) }, trackIndex);
                return;
            }

            await DropCurveAsync(drag, trackIndex);
        };
    }

    /// <summary>
    /// Перенос внутри планшета файла: кривая уходит с прежнего трека. На
    /// совместном планшете и между планшетами кривая копируется — исходный
    /// трек остаётся для сравнения.
    /// </summary>
    private bool IsInternalMove(DragInfo drag) =>
        drag.FromTrack is not null && drag.Tablet == documentKey && !data!.IsCombined;

    /// <summary>Почему кривую нельзя положить на этот планшет; <c>null</c> — можно.</summary>
    private string? DropRefusal(DragInfo drag)
    {
        if (data!.IsOwn(drag.File) || data.Find(drag.File) is not null)
        {
            return null;
        }

        if (Host?.FindDocument(drag.File) is not { } document)
        {
            return "Файл кривой не открыт";
        }

        return string.Equals(document.TabletData.IndexUnit.Trim(), data.IndexUnit.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Другая единица глубины: {document.TabletData.IndexUnit} и {data.IndexUnit}";
    }

    /// <summary>Ссылка на файл для кривой этого планшета: <c>null</c> — свой файл.</summary>
    private string? CurveSourceFor(string file) => data!.IsOwn(file) ? null : file;

    private bool WouldCreateComparisonTrack(int trackIndex, string? source)
    {
        string?[] sources = template!.Tracks[trackIndex].Curves.Select(curve => curve.Source).Distinct().ToArray();
        return sources.Length == 1 && sources[0] != source;
    }

    /// <summary>
    /// Кривая из дерева проекта, с другого трека совместного планшета или с
    /// соседнего планшета. Кривая другого файла ложится на трек сравнения
    /// (см. <see cref="TemplateEditing.PlaceCurve"/>).
    /// </summary>
    private async Task DropCurveAsync(DragInfo drag, int trackIndex)
    {
        string? source = CurveSourceFor(drag.File);
        if (source is not null && data!.Find(source) is null)
        {
            // Файл ещё не участвует в планшете: проверить единицу глубины и
            // скважину, а совместному планшету — добавить файл.
            if (Host?.FindDocument(source) is not { } document || content is null || !await Host.AcceptSourceAsync(content, document))
            {
                return;
            }

            data = Host.SourcesFor(content);
        }

        if (template is null || !HasCurve(source, drag.Mnemonic))
        {
            return;
        }

        CurveStyle curve = new() { Mnemonic = drag.Mnemonic, Source = source };
        // С другого трека этого же планшета кривая приходит со своим видом и шкалой.
        if (drag.Tablet == documentKey && drag.FromTrack is { } from && from < template.Tracks.Count
            && template.Tracks[from].Curves.FirstOrDefault(existing => TemplateEditing.SameCurve(existing, curve)) is { } original)
        {
            curve = original.Clone();
            curve.Scale = original.EffectiveScale(template.Tracks[from]);
        }

        if (TemplateEditing.PlaceCurve(template, trackIndex, curve, () => SuggestScale(source, drag.Mnemonic)) >= 0)
        {
            AfterEdit();
        }
    }

    private TrackScale SuggestScale(string? source, string mnemonic) =>
        (data!.Find(source)?.Data ?? (source is null ? null : Host?.FindDocument(source)?.TabletData)) is { } file
            ? DefaultTemplateBuilder.SuggestScale(file, mnemonic)
            : TrackScale.Linear(0, 100);

    private static void SetBackground(FrameworkElement element, Brush? brush)
    {
        switch (element)
        {
            case Border border:
                border.Background = brush;
                break;
            case Panel panel:
                panel.Background = brush;
                break;
        }
    }

    /// <param name="Tablet">Планшет, с которого тянут кривую; <c>null</c> — из дерева проекта.</param>
    /// <param name="File">Файл кривой — идентификатор движка.</param>
    private sealed record DragInfo(string? Tablet, string File, string Mnemonic, int? FromTrack);

    private static DragInfo? ParseDrag(DataPackageView view)
    {
        if (!view.Properties.TryGetValue(DragPropertyKey, out object? value) || value is not string text)
        {
            return null;
        }

        if (text.StartsWith("curve|", StringComparison.Ordinal))
        {
            string[] parts = text.Split('|', 3);
            return parts.Length == 3 ? new DragInfo(null, parts[1], parts[2], null) : null;
        }

        if (text.StartsWith("move|", StringComparison.Ordinal))
        {
            string[] parts = text.Split('|', 5);
            return parts.Length == 5 && int.TryParse(parts[2], out int from) ? new DragInfo(parts[1], parts[3], parts[4], from) : null;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Меню
    // ------------------------------------------------------------------

    private MenuFlyout DynamicMenu(Action<MenuFlyout> populate)
    {
        MenuFlyout menu = new();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (Dialogs is null || template is null || data is null)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "Редактирование недоступно", IsEnabled = false });
                return;
            }

            populate(menu);
        };
        return menu;
    }

    private void PopulateTrackMenu(MenuFlyout menu, int trackIndex)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        menu.Items.Add(Item($"Свойства трека {trackIndex + 1}…", async () => await EditTrackAsync(trackIndex)));
        MenuFlyoutItem copyTrack = Item($"Копировать трек {trackIndex + 1}", () => CopyTrack(trackIndex));
        copyTrack.IsEnabled = track.Curves.Count > 0 && Host is not null;
        menu.Items.Add(copyTrack);
        AddPasteMenu(menu, trackIndex);
        menu.Items.Add(new MenuFlyoutSeparator());

        // Кривые файлов планшета; если файлов несколько — по подменю на файл.
        MenuFlyoutSubItem add = new() { Text = "Добавить кривую" };
        foreach (TabletSource file in data!.All)
        {
            MenuFlyoutSubItem target = add;
            if (data.All.Count > 1)
            {
                target = new MenuFlyoutSubItem { Text = file.Name };
                add.Items.Add(target);
            }

            string? source = CurveSourceFor(file.Key);
            foreach (string mnemonic in file.Data.Mnemonics)
            {
                if (track.Curves.Any(curve => curve.Mnemonic == mnemonic && curve.Source == source))
                {
                    continue;
                }

                int elsewhere = template.Tracks.FindIndex(other => other.Curves.Any(curve => curve.Mnemonic == mnemonic && curve.Source == source));
                string unit = file.Data.Unit(mnemonic);
                string text = (unit.Length > 0 ? $"{mnemonic}, {unit}" : mnemonic) + (elsewhere >= 0 ? $"  (на треке {elsewhere + 1})" : string.Empty);
                target.Items.Add(Item(text, () => AddCurve(trackIndex, source, mnemonic)));
            }

            if (target.Items.Count == 0)
            {
                target.Items.Add(new MenuFlyoutItem { Text = "Все кривые файла уже на треке", IsEnabled = false });
            }
        }

        menu.Items.Add(add);

        MenuFlyoutItem addFill = Item("Добавить заливку…", async () => await EditFillAsync(trackIndex, null));
        addFill.IsEnabled = track.Curves.Count > 0;
        menu.Items.Add(addFill);
        if (track.Fills.Count > 0)
        {
            MenuFlyoutSubItem fills = new() { Text = "Заливки" };
            foreach (FillDefinition fill in track.Fills)
            {
                MenuFlyoutSubItem item = new() { Text = DescribeFill(fill) };
                item.Items.Add(Item("Изменить…", async () => await EditFillAsync(trackIndex, fill)));
                item.Items.Add(Item("Удалить", () =>
                {
                    track.Fills.Remove(fill);
                    AfterEdit();
                }));
                fills.Items.Add(item);
            }

            menu.Items.Add(fills);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Вставить трек слева", () => InsertTrack(trackIndex)));
        menu.Items.Add(Item("Вставить трек справа", () => InsertTrack(trackIndex + 1)));
        menu.Items.Add(Item("Удалить трек", async () => await DeleteTrackAsync(trackIndex)));
        AddDepthShiftItem(menu);
    }

    /// <summary>У совместного планшета — сдвиг файлов по глубине (увязка при показе).</summary>
    private void AddDepthShiftItem(MenuFlyout menu)
    {
        if (content is CombinedTablet combined && Host is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Сдвиг файлов по глубине…", async () => await Host.EditDepthShiftsAsync(combined)));
        }
    }

    private void PopulateCurveMenu(MenuFlyout menu, int trackIndex, CurveStyle style)
    {
        string mnemonic = style.Mnemonic;
        menu.Items.Add(Item($"Свойства кривой {mnemonic}…", async () => await EditCurveAsync(trackIndex, style)));
        MenuFlyoutItem copyCurve = Item($"Копировать кривую {mnemonic}", () => CopyCurve(trackIndex, style));
        copyCurve.IsEnabled = Host is not null;
        menu.Items.Add(copyCurve);

        MenuFlyoutSubItem move = new() { Text = "Перенести на трек" };
        for (int index = 0; index < template!.Tracks.Count; index++)
        {
            if (index == trackIndex)
            {
                continue;
            }

            int target = index;
            string curves = string.Join(", ", template.Tracks[index].Curves.Take(3).Select(curve => curve.Mnemonic));
            move.Items.Add(Item($"Трек {index + 1}{(curves.Length > 0 ? ": " + curves : " (пустой)")}", () => MoveCurve(trackIndex, style, target)));
        }

        move.Items.Add(new MenuFlyoutSeparator());
        move.Items.Add(Item("Новый трек справа", () =>
        {
            InsertTrack(trackIndex + 1, notify: false);
            MoveCurve(trackIndex, style, trackIndex + 1);
        }));
        menu.Items.Add(move);
        menu.Items.Add(Item("Убрать с трека", () => RemoveCurve(trackIndex, style)));

        // Шапка трека целиком занята строками кривых, поэтому команды трека
        // доступны и отсюда — иначе до них не дотянуться.
        menu.Items.Add(new MenuFlyoutSeparator());
        PopulateTrackMenu(menu, trackIndex);
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private static string DescribeFill(FillDefinition fill) => fill.Kind switch
    {
        FillKind.ToLeftEdge => $"{fill.Curve} ← левый край",
        FillKind.ToRightEdge => $"{fill.Curve} → правый край",
        FillKind.ToValue => $"{fill.Curve} ↔ {fill.Value.ToString("0.###", CultureInfo.CurrentCulture)}",
        _ => $"{fill.Curve} ↔ {fill.OtherCurve}"
    };

    // ------------------------------------------------------------------
    // Операции с шаблоном
    // ------------------------------------------------------------------

    private void AfterEdit()
    {
        Refresh();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Кривая из меню трека. В пустой трек она приходит со своей шкалой по
    /// данным — иначе легла бы на условную шкалу пустого трека.
    /// </summary>
    private void AddCurve(int trackIndex, string? source, string mnemonic)
    {
        if (data!.Series(source, mnemonic) is null)
        {
            return;
        }

        CurveStyle curve = new() { Mnemonic = mnemonic, Source = source };
        if (TemplateEditing.PlaceCurve(template!, trackIndex, curve, () => SuggestScale(source, mnemonic)) >= 0)
        {
            AfterEdit();
        }
    }

    /// <param name="key">Кривая: мнемоника и файл (<see cref="CurveStyle.Source"/>).</param>
    private void MoveCurve(int fromTrack, CurveStyle key, int toTrack)
    {
        string mnemonic = key.Mnemonic;
        if (fromTrack == toTrack || fromTrack >= template!.Tracks.Count || toTrack >= template.Tracks.Count)
        {
            return;
        }

        TrackDefinition source = template.Tracks[fromTrack];
        TrackDefinition target = template.Tracks[toTrack];
        CurveStyle? style = source.Curves.FirstOrDefault(curve => TemplateEditing.SameCurve(curve, key));
        if (style is null || target.Curves.Any(curve => TemplateEditing.SameCurve(curve, key)))
        {
            return;
        }

        source.Curves.Remove(style);
        source.Fills.RemoveAll(fill => fill.Source == key.Source && (fill.Curve == mnemonic || fill.OtherCurve == mnemonic));
        if (target.Curves.Count == 0)
        {
            target.Scale = style.EffectiveScale(source);
            style.Scale = null;
        }
        else if (style.Scale is null && target.Scale != source.Scale)
        {
            // Кривая сохраняет свою шкалу: перенос не должен менять её вид.
            style.Scale = source.Scale;
        }

        target.Curves.Add(style);
        AfterEdit();
    }

    private void RemoveCurve(int trackIndex, CurveStyle key)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        track.Curves.RemoveAll(curve => TemplateEditing.SameCurve(curve, key));
        track.Fills.RemoveAll(fill => fill.Source == key.Source && (fill.Curve == key.Mnemonic || fill.OtherCurve == key.Mnemonic));
        AfterEdit();
    }

    private void InsertTrack(int position, bool notify = true)
    {
        template!.Tracks.Insert(Math.Clamp(position, 0, template.Tracks.Count), new TrackDefinition { Scale = TrackScale.Linear(0, 100) });
        if (notify)
        {
            AfterEdit();
        }
    }

    private async Task DeleteTrackAsync(int trackIndex)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        if (track.Curves.Count > 0 && Dialogs is not null)
        {
            bool confirmed = await Dialogs.ConfirmAsync(
                "Удалить трек",
                $"Убрать с планшета трек {trackIndex + 1} с кривыми {string.Join(", ", track.Curves.Select(curve => curve.Mnemonic))}? "
                + "Данные файла не меняются: кривые можно вернуть из дерева проекта.",
                "Удалить",
                "Отмена",
                confirmIsDefault: false);
            if (!confirmed)
            {
                return;
            }
        }

        template.Tracks.RemoveAt(trackIndex);
        AfterEdit();
    }

    private async Task EditTrackAsync(int trackIndex)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        TabletDialogs.TrackSettings? settings = await TabletDialogs.EditTrackAsync(Dialogs!, track, trackIndex + 1);
        if (settings is null)
        {
            return;
        }

        track.Scale = settings.Scale;
        track.WidthDip = settings.Width;
        if (settings.ResetCurveScales)
        {
            track.Curves.ForEach(curve => curve.Scale = null);
        }

        AfterEdit();
    }

    private async Task EditCurveAsync(int trackIndex, CurveStyle key)
    {
        if (Dialogs is null)
        {
            return;
        }

        TrackDefinition track = template!.Tracks[trackIndex];
        int position = track.Curves.FindIndex(curve => TemplateEditing.SameCurve(curve, key));
        if (position < 0)
        {
            return;
        }

        CurveStyle? edited = await TabletDialogs.EditCurveAsync(Dialogs, track.Curves[position], track, data!.Series(key)?.Unit ?? string.Empty);
        if (edited is null)
        {
            return;
        }

        track.Curves[position] = edited;
        AfterEdit();
    }

    private async Task EditFillAsync(int trackIndex, FillDefinition? existing)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        // В списке заливки файл указан у каждой кривой: заливка строится только по кривым одного файла.
        FillDefinition? edited = await TabletDialogs.EditFillAsync(
            Dialogs!, track, existing, curve => data!.Label(curve.Source) is { } file ? $"{curve.Mnemonic} · {file}" : curve.Mnemonic);
        if (edited is null)
        {
            return;
        }

        int position = existing is null ? -1 : track.Fills.IndexOf(existing);
        if (position >= 0)
        {
            track.Fills[position] = edited;
        }
        else
        {
            track.Fills.Add(edited);
        }

        AfterEdit();
    }

    // ------------------------------------------------------------------
    // Буфер обмена: копирование и вставка кривых и треков
    // ------------------------------------------------------------------

    private void CopyCurve(int trackIndex, CurveStyle style)
    {
        if (Host is null)
        {
            return;
        }

        CurveStyle copy = TemplateEditing.CopyCurve(style, template!.Tracks[trackIndex], data!.Own?.Key);
        Host.Clipboard.Add(new CopiedCurve(copy, data.Find(style.Source)?.Name ?? string.Empty));
        Host.Notify($"Кривая {style.Mnemonic} скопирована · в буфере {Host.Clipboard.Count}");
    }

    private void CopyTrack(int trackIndex)
    {
        if (Host is null)
        {
            return;
        }

        TrackDefinition track = template!.Tracks[trackIndex];
        string files = string.Join(", ", track.Curves.Select(curve => data!.Find(curve.Source)?.Name).OfType<string>().Distinct());
        Host.Clipboard.Add(new CopiedTrack(TemplateEditing.CopyTrack(track, data!.Own?.Key), files));
        Host.Notify($"Трек {trackIndex + 1} скопирован · в буфере {Host.Clipboard.Count}");
    }

    /// <summary>
    /// Подменю «Вставить из буфера». У трека кривая накладывается на него или
    /// ложится на новый трек справа, трек вставляется справа или слева; у
    /// шапки глубины (<paramref name="trackIndex"/> = <c>null</c>) всё
    /// вставляется новыми треками в начало.
    /// </summary>
    private void AddPasteMenu(MenuFlyout menu, int? trackIndex)
    {
        if (Host is null)
        {
            return;
        }

        TabletClipboard clipboard = Host.Clipboard;
        MenuFlyoutSubItem paste = new() { Text = clipboard.Count > 0 ? $"Вставить из буфера ({clipboard.Count})" : "Вставить из буфера" };
        menu.Items.Add(paste);
        if (clipboard.Count == 0)
        {
            paste.Items.Add(new MenuFlyoutItem { Text = "Буфер пуст: скопируйте кривую или трек из их меню", IsEnabled = false });
            return;
        }

        int newPosition = trackIndex is { } current ? current + 1 : 0;
        string newTrackText = trackIndex is null ? "На новый трек" : "На новый трек справа";
        foreach (ClipboardEntry entry in clipboard.Entries)
        {
            MenuFlyoutSubItem item = new() { Text = entry.Title };
            switch (entry)
            {
                case CopiedCurve curve:
                    if (trackIndex is { } overlay)
                    {
                        item.Items.Add(Item($"Наложить на трек {overlay + 1}", async () => await PasteCurvesAsync([curve], overlay, newTrack: false)));
                    }

                    item.Items.Add(Item(newTrackText, async () => await PasteCurvesAsync([curve], newPosition, newTrack: true)));
                    break;
                case CopiedTrack track:
                    if (trackIndex is { } beside)
                    {
                        item.Items.Add(Item("Вставить справа", async () => await PasteTrackAsync(track, beside + 1)));
                        item.Items.Add(Item("Вставить слева", async () => await PasteTrackAsync(track, beside)));
                    }
                    else
                    {
                        item.Items.Add(Item("Вставить в начало", async () => await PasteTrackAsync(track, 0)));
                    }

                    break;
            }

            paste.Items.Add(item);
        }

        // «Все кривые» — в порядке копирования (в буфере новые первыми).
        List<CopiedCurve> curves = clipboard.Curves.Reverse().ToList();
        if (curves.Count > 1)
        {
            paste.Items.Add(new MenuFlyoutSeparator());
            if (trackIndex is { } all)
            {
                paste.Items.Add(Item($"Все кривые ({curves.Count}) — наложить на трек {all + 1}", async () => await PasteCurvesAsync(curves, all, newTrack: false)));
            }

            paste.Items.Add(Item($"Все кривые ({curves.Count}) — {newTrackText.ToLowerInvariant()}", async () => await PasteCurvesAsync(curves, newPosition, newTrack: true)));
        }

        paste.Items.Add(new MenuFlyoutSeparator());
        paste.Items.Add(Item("Очистить буфер", () =>
        {
            clipboard.Clear();
            Host.Notify("Буфер планшетов очищен");
        }));
    }

    /// <summary>
    /// Кривые из буфера: наложить на трек <paramref name="position"/> или
    /// (<paramref name="newTrack"/>) положить на новый трек на этом месте —
    /// первая задаёт его шкалу, остальные накладываются на неё.
    /// </summary>
    private async Task PasteCurvesAsync(IReadOnlyList<CopiedCurve> curves, int position, bool newTrack)
    {
        if (!await AcceptFilesAsync(curves.Select(curve => curve.Curve.Source!)) || template is null)
        {
            return;
        }

        int target = position;
        int placed = 0;
        foreach (CopiedCurve copied in curves)
        {
            CurveStyle curve = copied.Curve.Clone();
            curve.Source = CurveSourceFor(copied.Curve.Source!);
            if (newTrack && placed == 0)
            {
                target = TemplateEditing.CurveOnNewTrack(template, position, curve);
                placed++;
            }
            else if (TemplateEditing.OverlayCurve(template, target, curve) >= 0)
            {
                placed++;
            }
        }

        if (placed == 0)
        {
            Host?.Notify(curves.Count == 1 ? $"Кривая {curves[0].Curve.Mnemonic} уже на треке {target + 1}" : $"Эти кривые уже на треке {target + 1}");
            return;
        }

        AfterEdit();
        Host?.Notify(placed == 1 ? $"Кривая вставлена на трек {target + 1}" : $"Вставлено кривых на трек {target + 1}: {placed}");
    }

    private async Task PasteTrackAsync(CopiedTrack copied, int position)
    {
        if (!await AcceptFilesAsync(copied.Files) || template is null)
        {
            return;
        }

        int placed = TemplateEditing.PasteTrack(template, position, copied.Track, data!.Own?.Key);
        AfterEdit();
        Host?.Notify($"Трек вставлен: трек {placed + 1}");
    }

    /// <summary>
    /// Файлы кривых из буфера, которых ещё нет на планшете, проходят те же
    /// проверки, что при перетаскивании: файл открыт, единица глубины та же,
    /// при другой скважине — вопрос; совместному планшету файл добавляется.
    /// </summary>
    private async Task<bool> AcceptFilesAsync(IEnumerable<string> files)
    {
        foreach (string file in files.Distinct())
        {
            if (data!.IsOwn(file) || data.Find(file) is not null)
            {
                continue;
            }

            if (Host?.FindDocument(file) is not { } document || content is null)
            {
                Host?.Notify("Файл кривой из буфера уже закрыт");
                return false;
            }

            if (!await Host.AcceptSourceAsync(content, document))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Есть ли кривая в файле. Файл, на который шаблон ещё не ссылается, в
    /// <see cref="data"/> не входит — его кривые смотрятся в открытом файле.
    /// </summary>
    private bool HasCurve(string? source, string mnemonic) =>
        data!.Series(source, mnemonic) is not null
        || (source is not null && Host?.FindDocument(source)?.TabletData.Mnemonics.Contains(mnemonic) == true);

    // ------------------------------------------------------------------
    // Маркеры и интервалы
    // ------------------------------------------------------------------

    private void Body_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (data is null || annotations is null || Dialogs is null || pixelsPerUnit <= 0)
        {
            return;
        }

        Point position = e.GetPosition(bodyPanel);
        double depth = top + position.Y / pixelsPerUnit;
        string unit = data.IndexUnit.Length > 0 ? " " + data.IndexUnit : string.Empty;
        string depthText = depth.ToString("F2", CultureInfo.CurrentCulture) + unit;

        MenuFlyout menu = new();
        menu.Items.Add(Item($"Маркер на глубине {depthText}…", async () => await CreateMarkerAsync(depth)));
        menu.Items.Add(Item($"Интервал от {depthText}…", async () => await CreateIntervalAsync(depth, depth + bodyHeight / pixelsPerUnit * 0.1)));

        DepthMarker? marker = annotations.MarkerNear(depth, 5 / pixelsPerUnit);
        DepthInterval? interval = annotations.IntervalAt(depth);
        if (marker is not null || interval is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (marker is not null)
        {
            string name = marker.Name.Length > 0 ? $"«{marker.Name}»" : marker.Depth.ToString("F2", CultureInfo.CurrentCulture);
            menu.Items.Add(Item($"Маркер {name}…", async () => await EditMarkerAsync(marker)));
            menu.Items.Add(Item($"Удалить маркер {name}", () =>
            {
                annotations.Markers.Remove(marker);
                AfterEdit();
            }));
        }

        if (interval is not null)
        {
            string name = interval.Name.Length > 0 ? $"«{interval.Name}»" : "без названия";
            menu.Items.Add(Item($"Интервал {name}…", async () => await EditIntervalAsync(interval)));
            menu.Items.Add(Item($"Удалить интервал {name}", () =>
            {
                annotations.Intervals.Remove(interval);
                AfterEdit();
            }));
        }

        if (TrackAt(position.X) is { } trackIndex)
        {
            MenuFlyoutSubItem trackMenu = new() { Text = $"Трек {trackIndex + 1}" };
            MenuFlyout scratch = new();
            PopulateTrackMenu(scratch, trackIndex);
            foreach (MenuFlyoutItemBase item in scratch.Items.ToList())
            {
                scratch.Items.Remove(item);
                trackMenu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(trackMenu);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Shift + перетаскивание — выделить интервал", IsEnabled = false });
        menu.ShowAt(bodyPanel, new FlyoutShowOptions { Position = position });
        e.Handled = true;
    }

    /// <summary>Номер трека кривых под точкой тела планшета или null для трека глубины.</summary>
    private int? TrackAt(double x)
    {
        double left = template!.DepthTrackWidth;
        for (int index = 0; index < trackWidths.Length; index++)
        {
            if (x >= left && x < left + trackWidths[index])
            {
                return index;
            }

            left += trackWidths[index];
        }

        return null;
    }

    private async Task CreateMarkerAsync(double depth)
    {
        DepthMarker marker = new() { Depth = Math.Round(depth, 2) };
        if (await TabletDialogs.EditMarkerAsync(Dialogs!, marker, isNew: true, data!.IndexUnit) == TabletDialogs.AnnotationAction.Save)
        {
            annotations!.Markers.Add(marker);
            AfterEdit();
        }
    }

    private async Task EditMarkerAsync(DepthMarker marker)
    {
        switch (await TabletDialogs.EditMarkerAsync(Dialogs!, marker, isNew: false, data!.IndexUnit))
        {
            case TabletDialogs.AnnotationAction.Save:
                AfterEdit();
                break;
            case TabletDialogs.AnnotationAction.Delete:
                annotations!.Markers.Remove(marker);
                AfterEdit();
                break;
        }
    }

    private async Task CreateIntervalAsync(double from, double to)
    {
        if (Dialogs is null || annotations is null)
        {
            return;
        }

        DepthInterval interval = new() { Top = Math.Round(from, 2), Bottom = Math.Round(to, 2) };
        if (await TabletDialogs.EditIntervalAsync(Dialogs, interval, isNew: true, data!.IndexUnit) == TabletDialogs.AnnotationAction.Save)
        {
            annotations.Intervals.Add(interval);
            AfterEdit();
        }
    }

    private async Task EditIntervalAsync(DepthInterval interval)
    {
        switch (await TabletDialogs.EditIntervalAsync(Dialogs!, interval, isNew: false, data!.IndexUnit))
        {
            case TabletDialogs.AnnotationAction.Save:
                AfterEdit();
                break;
            case TabletDialogs.AnnotationAction.Delete:
                annotations!.Intervals.Remove(interval);
                AfterEdit();
                break;
        }
    }
}
