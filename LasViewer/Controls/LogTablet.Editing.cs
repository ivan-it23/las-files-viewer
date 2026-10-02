using System.Globalization;
using AMK.LasViewer.App.Dialogs;
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
    /// «curve|документ|мнемоника» (из дерева проекта) или
    /// «move|документ|трек|мнемоника» (из шапки трека).
    /// </summary>
    public const string DragPropertyKey = "lasviewer-drag";

    private static readonly SolidColorBrush HeaderBackground = new(Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));
    private static readonly SolidColorBrush HeaderBorderBrush = new(Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8));
    private static readonly SolidColorBrush MutedTextBrush = new(Color.FromArgb(0xFF, 0x66, 0x66, 0x66));
    private static readonly SolidColorBrush DropHighlight = new(Color.FromArgb(0xFF, 0xE3, 0xEE, 0xFB));

    private readonly List<Border> headerBorders = [];
    private readonly List<(TextBlock Text, string Mnemonic)> headerValueTexts = [];

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
        depthHeader.ContextFlyout = DynamicMenu(menu => menu.Items.Add(Item("Вставить трек", () => InsertTrack(0))));
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
                    Text = "Пустой трек: перетащите сюда кривую из дерева проекта",
                    FontSize = 11,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap
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
        bool present = data!.Values(style.Mnemonic) is not null;

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
            Text = style.Mnemonic + (style.Wrap == WrapMode.Wrap ? " ↻" : string.Empty),
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
        headerValueTexts.Add((valueText, style.Mnemonic));

        string unit = present ? data.Unit(style.Mnemonic) : "нет в файле";
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
        ToolTipService.SetToolTip(row, "Двойной щелчок — свойства кривой; перетащите на другой трек, чтобы перенести");
        row.ContextFlyout = DynamicMenu(menu => PopulateCurveMenu(menu, trackIndex, style.Mnemonic));
        row.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await EditCurveAsync(trackIndex, style.Mnemonic);
        };
        row.DragStarting += (_, e) =>
        {
            e.Data.Properties[DragPropertyKey] = $"move|{documentKey}|{trackIndex}|{style.Mnemonic}";
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        return row;
    }

    private static TextBlock ScaleText(string text, HorizontalAlignment alignment, int column)
    {
        TextBlock block = new() { Text = text, FontSize = 10, Foreground = MutedTextBrush, HorizontalAlignment = alignment };
        Grid.SetColumn(block, column);
        return block;
    }

    private void UpdateHeaderValues(int row)
    {
        foreach ((TextBlock text, string mnemonic) in headerValueTexts)
        {
            double[]? values = data?.Values(mnemonic);
            text.Text = row >= 0 && values is not null ? FormatValue(values[row]) : string.Empty;
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
            if (ParseDrag(e.DataView) is { } drag && drag.Document == documentKey && Dialogs is not null)
            {
                e.AcceptedOperation = drag.FromTrack is null ? DataPackageOperation.Copy : DataPackageOperation.Move;
                e.DragUIOverride.Caption = drag.FromTrack is null ? $"Добавить {drag.Mnemonic} на трек {trackIndex + 1}" : $"Перенести {drag.Mnemonic} на трек {trackIndex + 1}";
                SetBackground(target, DropHighlight);
            }
        };
        target.DragLeave += (_, _) => SetBackground(target, original);
        target.Drop += (_, e) =>
        {
            SetBackground(target, original);
            if (ParseDrag(e.DataView) is not { } drag || drag.Document != documentKey)
            {
                return;
            }

            if (drag.FromTrack is { } from)
            {
                MoveCurve(from, drag.Mnemonic, trackIndex);
            }
            else
            {
                AddCurve(trackIndex, drag.Mnemonic);
            }
        };
    }

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

    private sealed record DragInfo(string Document, string Mnemonic, int? FromTrack);

    private static DragInfo? ParseDrag(DataPackageView view)
    {
        if (!view.Properties.TryGetValue(DragPropertyKey, out object? value) || value is not string text)
        {
            return null;
        }

        string[] parts = text.Split('|');
        return parts switch
        {
            ["curve", var document, var mnemonic] => new DragInfo(document, mnemonic, null),
            ["move", var document, var track, var mnemonic] when int.TryParse(track, out int from) => new DragInfo(document, mnemonic, from),
            _ => null
        };
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

        MenuFlyoutSubItem add = new() { Text = "Добавить кривую" };
        foreach (string mnemonic in data!.Mnemonics)
        {
            if (track.Curves.Any(curve => curve.Mnemonic == mnemonic))
            {
                continue;
            }

            int elsewhere = template.Tracks.FindIndex(other => other.Curves.Any(curve => curve.Mnemonic == mnemonic));
            string unit = data.Unit(mnemonic);
            string text = (unit.Length > 0 ? $"{mnemonic}, {unit}" : mnemonic) + (elsewhere >= 0 ? $"  (на треке {elsewhere + 1})" : string.Empty);
            add.Items.Add(Item(text, () => AddCurve(trackIndex, mnemonic)));
        }

        if (add.Items.Count == 0)
        {
            add.Items.Add(new MenuFlyoutItem { Text = "Все кривые файла уже на треке", IsEnabled = false });
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
    }

    private void PopulateCurveMenu(MenuFlyout menu, int trackIndex, string mnemonic)
    {
        menu.Items.Add(Item($"Свойства кривой {mnemonic}…", async () => await EditCurveAsync(trackIndex, mnemonic)));

        MenuFlyoutSubItem move = new() { Text = "Перенести на трек" };
        for (int index = 0; index < template!.Tracks.Count; index++)
        {
            if (index == trackIndex)
            {
                continue;
            }

            int target = index;
            string curves = string.Join(", ", template.Tracks[index].Curves.Take(3).Select(curve => curve.Mnemonic));
            move.Items.Add(Item($"Трек {index + 1}{(curves.Length > 0 ? ": " + curves : " (пустой)")}", () => MoveCurve(trackIndex, mnemonic, target)));
        }

        move.Items.Add(new MenuFlyoutSeparator());
        move.Items.Add(Item("Новый трек справа", () =>
        {
            InsertTrack(trackIndex + 1, notify: false);
            MoveCurve(trackIndex, mnemonic, trackIndex + 1);
        }));
        menu.Items.Add(move);
        menu.Items.Add(Item("Убрать с трека", () => RemoveCurve(trackIndex, mnemonic)));

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

    private void AddCurve(int trackIndex, string mnemonic)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        if (track.Curves.Any(curve => curve.Mnemonic == mnemonic) || data!.Values(mnemonic) is null)
        {
            return;
        }

        // В пустой трек кривая приходит со своей шкалой по данным — иначе она
        // легла бы на условную шкалу пустого трека.
        if (track.Curves.Count == 0)
        {
            track.Scale = DefaultTemplateBuilder.SuggestScale(data, mnemonic);
        }

        track.Curves.Add(new CurveStyle { Mnemonic = mnemonic, Color = CurvePalette.ForPosition(track.Curves.Count) });
        AfterEdit();
    }

    private void MoveCurve(int fromTrack, string mnemonic, int toTrack)
    {
        if (fromTrack == toTrack || fromTrack >= template!.Tracks.Count || toTrack >= template.Tracks.Count)
        {
            return;
        }

        TrackDefinition source = template.Tracks[fromTrack];
        TrackDefinition target = template.Tracks[toTrack];
        CurveStyle? style = source.Curves.FirstOrDefault(curve => curve.Mnemonic == mnemonic);
        if (style is null || target.Curves.Any(curve => curve.Mnemonic == mnemonic))
        {
            return;
        }

        source.Curves.Remove(style);
        source.Fills.RemoveAll(fill => fill.Curve == mnemonic || fill.OtherCurve == mnemonic);
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

    private void RemoveCurve(int trackIndex, string mnemonic)
    {
        TrackDefinition track = template!.Tracks[trackIndex];
        track.Curves.RemoveAll(curve => curve.Mnemonic == mnemonic);
        track.Fills.RemoveAll(fill => fill.Curve == mnemonic || fill.OtherCurve == mnemonic);
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

    private async Task EditCurveAsync(int trackIndex, string mnemonic)
    {
        if (Dialogs is null)
        {
            return;
        }

        TrackDefinition track = template!.Tracks[trackIndex];
        int position = track.Curves.FindIndex(curve => curve.Mnemonic == mnemonic);
        if (position < 0)
        {
            return;
        }

        CurveStyle? edited = await TabletDialogs.EditCurveAsync(Dialogs, track.Curves[position], track, data!.Unit(mnemonic));
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
        FillDefinition? edited = await TabletDialogs.EditFillAsync(Dialogs!, track, existing);
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
