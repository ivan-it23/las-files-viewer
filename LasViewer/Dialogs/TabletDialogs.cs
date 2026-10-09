using System.Globalization;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>Диалоги настройки планшета. Каждый возвращает изменённую копию или <c>null</c> при отмене.</summary>
internal static class TabletDialogs
{
    private static readonly string[] ScaleKinds = ["Линейная", "Логарифмическая"];
    private static readonly string[] Dashes = ["Сплошная", "Штрих", "Точки"];

    // ------------------------------------------------------------------
    // Кривая
    // ------------------------------------------------------------------

    public static async Task<CurveStyle?> EditCurveAsync(DialogService dialogs, CurveStyle style, TrackDefinition track, string unit)
    {
        TrackScale current = style.EffectiveScale(track);
        Form form = new();
        ColorSwatchPicker color = form.Add("Цвет", new ColorSwatchPicker(style.Color));
        NumberBox thickness = form.Add("Толщина", Form.Number(style.Thickness, 0.5, 6, 0.5));
        thickness.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 1 };
        ComboBox dash = form.Add("Линия", Form.Choice(Dashes, (int)style.Dash));
        CheckBox ownScale = form.Add("Шкала", new CheckBox { Content = "Своя шкала кривой (иначе — шкала трека)", IsChecked = style.Scale is not null });
        ComboBox kind = form.Add("Вид шкалы", Form.Choice(ScaleKinds, current.Kind == ScaleKind.Logarithmic ? 1 : 0));
        NumberBox left = form.Add("Левая граница", Form.Number(current.Left));
        NumberBox right = form.Add("Правая граница", Form.Number(current.Right));
        CheckBox wrap = form.Add("За шкалой", new CheckBox
        {
            Content = "Переносить на следующий оборот шкалы (пунктиром)",
            IsChecked = style.Wrap == WrapMode.Wrap
        });

        void UpdateScaleFields()
        {
            bool own = ownScale.IsChecked == true;
            kind.IsEnabled = left.IsEnabled = right.IsEnabled = own;
        }

        ownScale.Checked += (_, _) => UpdateScaleFields();
        ownScale.Unchecked += (_, _) => UpdateScaleFields();
        UpdateScaleFields();

        CurveStyle? result = null;
        ContentDialog dialog = Dialog($"Кривая {style.Mnemonic}{(unit.Length > 0 ? ", " + unit : string.Empty)}", form, "Применить");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            TrackScale? scale = null;
            if (ownScale.IsChecked == true)
            {
                scale = BuildScale(kind.SelectedIndex, Form.Read(left), Form.Read(right), out string? error);
                if (scale is null)
                {
                    form.ShowError(error);
                    args.Cancel = true;
                    return;
                }
            }

            result = style.Clone();
            result.Color = color.Value;
            result.Thickness = double.IsNaN(thickness.Value) ? style.Thickness : thickness.Value;
            result.Dash = (LineDash)Math.Max(0, dash.SelectedIndex);
            result.Scale = scale;
            result.Wrap = wrap.IsChecked == true ? WrapMode.Wrap : WrapMode.Clamp;
        };

        await dialogs.ShowAsync(dialog);
        return result;
    }

    // ------------------------------------------------------------------
    // Трек
    // ------------------------------------------------------------------

    public sealed record TrackSettings(TrackScale Scale, double Width, bool ResetCurveScales);

    public static async Task<TrackSettings?> EditTrackAsync(DialogService dialogs, TrackDefinition track, int number)
    {
        Form form = new();
        ComboBox kind = form.Add("Вид шкалы", Form.Choice(ScaleKinds, track.Scale.Kind == ScaleKind.Logarithmic ? 1 : 0));
        NumberBox left = form.Add("Левая граница", Form.Number(track.Scale.Left));
        NumberBox right = form.Add("Правая граница", Form.Number(track.Scale.Right));
        NumberBox width = form.Add("Ширина, DIP", Form.Number(track.WidthDip, 80, 800, 10));
        CheckBox reset = form.Add("Кривые", new CheckBox
        {
            Content = "Убрать собственные шкалы кривых — все по шкале трека",
            IsChecked = false,
            IsEnabled = track.Curves.Any(curve => curve.Scale is not null)
        });

        TrackSettings? result = null;
        ContentDialog dialog = Dialog($"Трек {number}", form, "Применить");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            TrackScale? scale = BuildScale(kind.SelectedIndex, Form.Read(left), Form.Read(right), out string? error);
            if (scale is null)
            {
                form.ShowError(error);
                args.Cancel = true;
                return;
            }

            double widthValue = double.IsNaN(width.Value) ? track.WidthDip : Math.Clamp(width.Value, 80, 800);
            result = new TrackSettings(scale, widthValue, reset.IsChecked == true);
        };

        await dialogs.ShowAsync(dialog);
        return result;
    }

    // ------------------------------------------------------------------
    // Заливка
    // ------------------------------------------------------------------

    private static readonly string[] FillKinds = ["От левого края до кривой", "От кривой до правого края", "Между кривой и значением", "Между двумя кривыми"];

    /// <param name="title">Подпись кривой в списке: мнемоника и, для кривой другого файла, имя файла.</param>
    public static async Task<FillDefinition?> EditFillAsync(DialogService dialogs, TrackDefinition track, FillDefinition? existing, Func<CurveStyle, string> title)
    {
        if (track.Curves.Count == 0)
        {
            await dialogs.ShowMessageAsync("Заливка", "На треке нет кривых: заливка строится от кривой.");
            return null;
        }

        CurveStyle[] curves = [.. track.Curves];
        string[] titles = curves.Select(title).ToArray();
        int Find(string? mnemonic, string? source) =>
            Math.Max(0, Array.FindIndex(curves, item => item.Mnemonic == mnemonic && item.Source == source));

        // Вторая кривая по умолчанию — последняя из того же файла, что и первая.
        int defaultOther = Array.FindLastIndex(curves, item => item.Source == curves[0].Source);
        bool canPair = defaultOther > 0;
        FillDefinition source = existing ?? new FillDefinition
        {
            Kind = canPair ? FillKind.BetweenCurves : FillKind.ToLeftEdge,
            Curve = curves[0].Mnemonic,
            OtherCurve = canPair ? curves[defaultOther].Mnemonic : null,
            Source = curves[0].Source,
            Color = SceneColors.WithAlpha(curves[0].Color, 0x55),
            OppositeColor = canPair ? SceneColors.WithAlpha(curves[defaultOther].Color, 0x55) : null
        };

        Form form = new();
        ComboBox kind = form.Add("Вид", Form.Choice(FillKinds, (int)source.Kind));
        ComboBox curve = form.Add("Кривая", Form.Choice(titles, Find(source.Curve, source.Source)));
        ComboBox other = form.Add("Вторая кривая", Form.Choice(titles, source.OtherCurve is null ? curves.Length - 1 : Find(source.OtherCurve, source.Source)));
        NumberBox value = form.Add("Значение", Form.Number(source.Value));
        ColorSwatchPicker color = form.Add("Цвет", new ColorSwatchPicker(source.Color));
        CheckBox bothSides = form.Add("Другая сторона", new CheckBox
        {
            Content = "Заливать и там, где кривая левее границы",
            IsChecked = source.OppositeColor is not null
        });
        ColorSwatchPicker opposite = form.Add("Цвет другой стороны", new ColorSwatchPicker(source.OppositeColor ?? 0xFFD62728));
        TextBlock hint = form.Add(string.Empty, new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = "Где кривая правее границы — основной цвет, где левее — цвет другой стороны. "
                   + "Так видны пересечения кривых."
        });

        void Update()
        {
            FillKind selected = (FillKind)Math.Max(0, kind.SelectedIndex);
            bool sided = selected is FillKind.ToValue or FillKind.BetweenCurves;
            form.SetVisible(other, selected == FillKind.BetweenCurves);
            form.SetVisible(value, selected == FillKind.ToValue);
            form.SetVisible(bothSides, sided);
            form.SetVisible(opposite, sided && bothSides.IsChecked == true);
            form.SetVisible(hint, sided);
        }

        kind.SelectionChanged += (_, _) => Update();
        bothSides.Checked += (_, _) => Update();
        bothSides.Unchecked += (_, _) => Update();
        Update();

        FillDefinition? result = null;
        ContentDialog dialog = Dialog(existing is null ? "Новая заливка" : "Заливка", form, existing is null ? "Добавить" : "Применить");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            FillKind selected = (FillKind)Math.Max(0, kind.SelectedIndex);
            if (selected == FillKind.BetweenCurves && curve.SelectedIndex == other.SelectedIndex)
            {
                form.ShowError("Для заливки между кривыми выберите две разные кривые.");
                args.Cancel = true;
                return;
            }

            if (selected == FillKind.BetweenCurves && curves[curve.SelectedIndex].Source != curves[other.SelectedIndex].Source)
            {
                // У кривых разных файлов разные сетки глубин: заливка между ними
                // потребовала бы пересчёта, а его в программе нет.
                form.ShowError("Заливка между кривыми разных файлов не строится. Выберите кривые одного файла.");
                args.Cancel = true;
                return;
            }

            if (selected == FillKind.ToValue && double.IsNaN(value.Value))
            {
                form.ShowError("Укажите граничное значение.");
                args.Cancel = true;
                return;
            }

            bool sided = selected is FillKind.ToValue or FillKind.BetweenCurves;
            result = new FillDefinition
            {
                Kind = selected,
                Curve = curves[curve.SelectedIndex].Mnemonic,
                OtherCurve = selected == FillKind.BetweenCurves ? curves[other.SelectedIndex].Mnemonic : null,
                Source = curves[curve.SelectedIndex].Source,
                Value = selected == FillKind.ToValue ? value.Value : 0,
                // Заливка полупрозрачная, чтобы кривые и сетка под ней оставались видны.
                Color = SceneColors.WithAlpha(color.Value, 0x55),
                OppositeColor = sided && bothSides.IsChecked == true ? SceneColors.WithAlpha(opposite.Value, 0x55) : null
            };
        };

        await dialogs.ShowAsync(dialog);
        return result;
    }

    // ------------------------------------------------------------------
    // Маркер и интервал
    // ------------------------------------------------------------------

    public enum AnnotationAction
    {
        Cancel,
        Save,
        Delete
    }

    public static async Task<AnnotationAction> EditMarkerAsync(DialogService dialogs, DepthMarker marker, bool isNew, string unit)
    {
        Form form = new();
        TextBox name = form.Add("Название", new TextBox { Text = marker.Name, PlaceholderText = "например, кровля пласта" });
        NumberBox depth = form.Add($"Глубина, {unit}", DepthBox(marker.Depth));
        ColorSwatchPicker color = form.Add("Цвет", new ColorSwatchPicker(marker.Color));
        TextBox note = form.Add("Примечание", Note(marker.Note));

        ContentDialog dialog = Dialog(isNew ? "Новый маркер" : "Маркер", form, isNew ? "Добавить" : "Применить");
        if (!isNew)
        {
            dialog.SecondaryButtonText = "Удалить";
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (double.IsNaN(depth.Value))
            {
                form.ShowError("Укажите глубину.");
                args.Cancel = true;
            }
        };

        ContentDialogResult answer = await dialogs.ShowAsync(dialog);
        if (answer == ContentDialogResult.Secondary)
        {
            return AnnotationAction.Delete;
        }

        if (answer != ContentDialogResult.Primary)
        {
            return AnnotationAction.Cancel;
        }

        marker.Name = name.Text.Trim();
        marker.Depth = depth.Value;
        marker.Color = color.Value;
        marker.Note = note.Text.Trim();
        return AnnotationAction.Save;
    }

    public static async Task<AnnotationAction> EditIntervalAsync(DialogService dialogs, DepthInterval interval, bool isNew, string unit)
    {
        Form form = new();
        TextBox name = form.Add("Название", new TextBox { Text = interval.Name, PlaceholderText = "например, пласт БС10" });
        NumberBox top = form.Add($"Кровля, {unit}", DepthBox(Math.Min(interval.Top, interval.Bottom)));
        NumberBox bottom = form.Add($"Подошва, {unit}", DepthBox(Math.Max(interval.Top, interval.Bottom)));
        ColorSwatchPicker color = form.Add("Цвет", new ColorSwatchPicker(interval.Color));
        TextBox note = form.Add("Примечание", Note(interval.Note));

        ContentDialog dialog = Dialog(isNew ? "Новый интервал" : "Интервал", form, isNew ? "Добавить" : "Применить");
        if (!isNew)
        {
            dialog.SecondaryButtonText = "Удалить";
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (double.IsNaN(top.Value) || double.IsNaN(bottom.Value) || bottom.Value <= top.Value)
            {
                form.ShowError("Подошва должна быть глубже кровли.");
                args.Cancel = true;
            }
        };

        ContentDialogResult answer = await dialogs.ShowAsync(dialog);
        if (answer == ContentDialogResult.Secondary)
        {
            return AnnotationAction.Delete;
        }

        if (answer != ContentDialogResult.Primary)
        {
            return AnnotationAction.Cancel;
        }

        interval.Name = name.Text.Trim();
        interval.Top = top.Value;
        interval.Bottom = bottom.Value;
        interval.Color = color.Value;
        interval.Note = note.Text.Trim();
        return AnnotationAction.Save;
    }

    // ------------------------------------------------------------------

    private static ContentDialog Dialog(string title, Form form, string primary)
    {
        return new ContentDialog
        {
            Title = title,
            Content = form.Root,
            PrimaryButtonText = primary,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
    }

    private static NumberBox DepthBox(double value)
    {
        NumberBox box = Form.Number(Math.Round(value, 2), step: 0.1);
        box.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 2 };
        return box;
    }

    private static TextBox Note(string text) => new()
    {
        Text = text,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 70
    };

    /// <summary>Шкала по полям диалога или <c>null</c> с объяснением, что не так.</summary>
    private static TrackScale? BuildScale(int kindIndex, double left, double right, out string? error)
    {
        error = null;
        if (double.IsNaN(left) || double.IsNaN(right))
        {
            error = "Укажите обе границы шкалы.";
            return null;
        }

        if (left == right)
        {
            error = "Границы шкалы должны различаться.";
            return null;
        }

        if (kindIndex == 1)
        {
            if (left <= 0 || right <= 0)
            {
                error = "На логарифмической шкале границы должны быть больше нуля.";
                return null;
            }

            return TrackScale.Logarithmic(left, right);
        }

        return TrackScale.Linear(left, right);
    }

    public static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
