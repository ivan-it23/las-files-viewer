using System.Globalization;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>Что и в каком масштабе выводить в файл или на печать.</summary>
internal sealed record ExportOptions(double Top, double Bottom, double PixelsPerUnit, double Ratio);

internal static class ExportDialog
{
    private static readonly double[] Ratios = [100, 200, 500, 1000, 2000, 5000];

    // Высота тела страницы A4 в книжной ориентации за вычетом полей, заголовка
    // и шапки — для оценки числа страниц до выбора принтера.
    private const double EstimatedBodyMillimetres = 297 - 20 - 45;

    /// <param name="visibleTop">Верх видимого участка планшета.</param>
    /// <param name="visibleBottom">Низ видимого участка планшета.</param>
    public static async Task<ExportOptions?> ShowAsync(
        DialogService dialogs,
        string title,
        string confirmText,
        TabletSources data,
        double visibleTop,
        double visibleBottom,
        double currentRatio,
        bool estimatePages)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string unit = data.IndexUnit;
        Form form = new();
        ComboBox range = form.Add("Интервал", Form.Choice(
        [
            $"Вся запись: {data.Top.ToString("0.##", culture)} – {data.Bottom.ToString("0.##", culture)} {unit}",
            $"Видимый участок: {visibleTop.ToString("0.##", culture)} – {visibleBottom.ToString("0.##", culture)} {unit}",
            "Заданный интервал"
        ], 0));
        NumberBox from = form.Add($"Кровля, {unit}", Form.Number(Math.Round(visibleTop, 2), step: 1));
        NumberBox to = form.Add($"Подошва, {unit}", Form.Number(Math.Round(visibleBottom, 2), step: 1));
        List<string> scaleItems = [$"Как на экране, 1:{Math.Round(currentRatio).ToString("N0", culture)}"];
        scaleItems.AddRange(Ratios.Select(ratio => "1:" + ratio.ToString("N0", culture)));
        ComboBox scale = form.Add("Масштаб", Form.Choice(scaleItems, 0));
        TextBlock estimate = form.Add(string.Empty, new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });

        (double Top, double Bottom) Interval() => range.SelectedIndex switch
        {
            1 => (visibleTop, visibleBottom),
            2 => (Math.Min(from.Value, to.Value), Math.Max(from.Value, to.Value)),
            _ => (data.Top, data.Bottom)
        };

        double Ratio() => scale.SelectedIndex <= 0 ? currentRatio : Ratios[scale.SelectedIndex - 1];

        void Update()
        {
            form.SetVisible(from, range.SelectedIndex == 2);
            form.SetVisible(to, range.SelectedIndex == 2);
            (double top, double bottom) = Interval();
            if (double.IsNaN(top) || double.IsNaN(bottom) || bottom <= top)
            {
                estimate.Text = string.Empty;
                return;
            }

            double millimetres = (bottom - top) * DepthViewport.PixelsPerUnitForRatio(Ratio(), unit) / DepthViewport.DipPerMillimetre;
            string length = millimetres >= 1000
                ? (millimetres / 1000).ToString("0.00", culture) + " м"
                : millimetres.ToString("0", culture) + " мм";
            estimate.Text = estimatePages
                ? $"Длина планшета на бумаге: {length}; около {Math.Max(1, (int)Math.Ceiling(millimetres / EstimatedBodyMillimetres))} стр. A4 в книжной ориентации."
                : $"Длина планшета: {length}.";
        }

        range.SelectionChanged += (_, _) => Update();
        scale.SelectionChanged += (_, _) => Update();
        from.ValueChanged += (_, _) => Update();
        to.ValueChanged += (_, _) => Update();
        Update();

        ExportOptions? result = null;
        ContentDialog dialog = new()
        {
            Title = title,
            Content = form.Root,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            (double top, double bottom) = Interval();
            if (double.IsNaN(top) || double.IsNaN(bottom) || bottom - top <= 0)
            {
                form.ShowError("Подошва интервала должна быть глубже кровли.");
                args.Cancel = true;
                return;
            }

            double ratio = Ratio();
            result = new ExportOptions(top, bottom, DepthViewport.PixelsPerUnitForRatio(ratio, unit), ratio);
        };

        await dialogs.ShowAsync(dialog);
        return result;
    }
}
