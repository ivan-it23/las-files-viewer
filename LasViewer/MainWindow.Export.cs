using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AMK.LasViewer.App.Controls;
using AMK.LasViewer.App.Dialogs;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using AMK.LasViewer.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace AMK.LasViewer.App;

/// <summary>Экспорт планшета в SVG и PNG и печать.</summary>
public sealed partial class MainWindow
{
    private TabletPrinter? printer;

    /// <summary>Планшет в фокусе — файл или совместный — и его файлы.</summary>
    private sealed record TabletTarget(ITabletContent Content, LogTablet Tablet, TabletSources Sources)
    {
        public string FileStem => Content is OpenedDocument document
            ? Path.GetFileNameWithoutExtension(document.Document.Name)
            : Content.Title;
    }

    private async Task<TabletTarget?> RequireTabletAsync()
    {
        if (Panels.FocusedContent is { } content && Panels.FocusedTablet is { HasData: true } tablet)
        {
            return new TabletTarget(content, tablet, project.SourcesFor(content));
        }

        await dialogs.ShowMessageAsync("Планшет", "Откройте LAS-файл: выводится планшет активного файла.");
        return null;
    }

    private static (double Top, double Bottom) VisibleRange(LogTablet tablet) =>
        (tablet.Top, tablet.Top + tablet.BodyHeight / tablet.PixelsPerUnit);

    // ------------------------------------------------------------------
    // SVG
    // ------------------------------------------------------------------

    private async void ExportSvgMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (await RequireTabletAsync() is not { } target)
        {
            return;
        }

        LogTablet tablet = target.Tablet;
        (double visibleTop, double visibleBottom) = VisibleRange(tablet);
        ExportOptions? options = await ExportDialog.ShowAsync(
            dialogs, "Экспорт планшета в SVG", "Далее", target.Sources, visibleTop, visibleBottom, tablet.Ratio, estimatePages: false);
        if (options is null)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = target.FileStem + " планшет"
        };
        picker.FileTypeChoices.Add("Векторное изображение SVG", [".svg"]);
        StorageFile? file = await FilePickers.PickSaveFileAsync(picker, this, dialogs);
        if (file is null)
        {
            return;
        }

        try
        {
            TabletPage page = TabletPageBuilder.Build(
                target.Sources,
                target.Content.Template,
                target.Content.Annotations,
                TitleLines(target, options, $"Экспорт {DateTime.Now:dd.MM.yyyy HH:mm}"),
                tablet.TrackWidths,
                options.Top,
                options.Bottom,
                options.PixelsPerUnit);
            string svg = SvgWriter.Write(page.Scene, page.Width, page.Height, target.Content.Title);
            await File.WriteAllTextAsync(file.Path, svg, new System.Text.UTF8Encoding(false));
            SetStatus($"Планшет сохранён: {file.Path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Планшет не сохранён", $"{file.Path}\n\n{exception.Message}");
        }
    }

    // ------------------------------------------------------------------
    // PNG
    // ------------------------------------------------------------------

    private async void ExportPngMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (await RequireTabletAsync() is not { } target)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = target.FileStem + " снимок"
        };
        picker.FileTypeChoices.Add("Изображение PNG", [".png"]);
        StorageFile? file = await FilePickers.PickSaveFileAsync(picker, this, dialogs);
        if (file is null)
        {
            return;
        }

        try
        {
            // Снимок того, что видно на экране, в разрешении экрана.
            RenderTargetBitmap bitmap = new();
            await bitmap.RenderAsync(target.Tablet);
            IBuffer pixels = await bitmap.GetPixelsAsync();
            double dpi = 96 * (Content.XamlRoot?.RasterizationScale ?? 1);

            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            stream.Size = 0;
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                dpi,
                dpi,
                pixels.ToArray());
            await encoder.FlushAsync();
            SetStatus($"Снимок сохранён: {file.Path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await dialogs.ShowMessageAsync("Снимок не сохранён", $"{file.Path}\n\n{exception.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Печать
    // ------------------------------------------------------------------

    private async void PrintMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (await RequireTabletAsync() is not { } target)
        {
            return;
        }

        (double visibleTop, double visibleBottom) = VisibleRange(target.Tablet);
        ExportOptions? options = await ExportDialog.ShowAsync(
            dialogs, "Печать планшета", "Далее", target.Sources, visibleTop, visibleBottom, target.Tablet.Ratio, estimatePages: true);
        if (options is null)
        {
            return;
        }

        printer ??= new TabletPrinter(this);
        try
        {
            await printer.PrintAsync(target.Content.Title, description => BuildPrintPages(target, options, description));
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Write($"Печать не началась: {exception}");
            await dialogs.ShowMessageAsync(
                "Печать недоступна",
                "Windows не открыл диалог печати. Проверьте, что в системе есть хотя бы один принтер "
                + "(для PDF — «Microsoft Print to PDF»), или сохраните планшет в SVG.\n\n" + exception.Message);
        }
    }

    private IReadOnlyList<UIElement> BuildPrintPages(TabletTarget target, ExportOptions options, PrintPageDescription description)
    {
        Size pageSize = description.PageSize;
        Rect area = description.ImageableRect;
        if (area.Width <= 0 || area.Height <= 0)
        {
            // Драйвер не сообщил область печати — поля по 10 мм.
            double margin = 10 * DepthViewport.DipPerMillimetre;
            area = new Rect(margin, margin, pageSize.Width - 2 * margin, pageSize.Height - 2 * margin);
        }

        // Строка «Страница N из M» в заголовке есть на каждой странице, поэтому
        // высота заголовка известна до того, как известно число страниц.
        IReadOnlyList<string> sample = TitleLines(target, options, "Страница 1 из 1");
        TabletTemplate template = target.Content.Template;
        double[] widths = TabletPageBuilder.ScaleTrackWidths(template, area.Width);
        double bodyHeight = TabletPageBuilder.BodyHeight(template, sample, area.Height, target.Sources);
        IReadOnlyList<(double Top, double Bottom)> ranges = TabletPageBuilder.SplitDepth(options.Top, options.Bottom, options.PixelsPerUnit, bodyHeight);

        List<UIElement> pages = [];
        for (int index = 0; index < ranges.Count; index++)
        {
            string footer = $"Страница {index + 1} из {ranges.Count} · напечатано {DateTime.Now:dd.MM.yyyy HH:mm}";
            TabletPage page = TabletPageBuilder.Build(
                target.Sources,
                template,
                target.Content.Annotations,
                TitleLines(target, options, footer),
                widths,
                ranges[index].Top,
                ranges[index].Bottom,
                options.PixelsPerUnit);

            Canvas content = new() { Width = page.Width, Height = page.Height };
            Canvas.SetLeft(content, area.X);
            Canvas.SetTop(content, area.Y);
            foreach (SceneLayer layer in page.Scene.Layers)
            {
                content.Children.Add(SceneRenderer.CreateLayerCanvas(layer));
            }

            Canvas sheet = new() { Width = pageSize.Width, Height = pageSize.Height, Background = new SolidColorBrush(Colors.White) };
            sheet.Children.Add(content);
            pages.Add(sheet);
        }

        return pages;
    }

    /// <summary>
    /// Заголовок страницы: скважина, файл (у совместного планшета — его
    /// название и файлы), интервал и масштаб. Сведения о скважине — из
    /// первого файла.
    /// </summary>
    private IReadOnlyList<string> TitleLines(TabletTarget target, ExportOptions options, string footer)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        OpenedDocument document = target.Content as OpenedDocument
                                  ?? project.FindByFileId(target.Sources.Primary.Key)
                                  ?? throw new InvalidOperationException("Файл планшета закрыт.");
        LasSection? well = document.Document.Section(LasSectionKind.Well);
        string Value(string mnemonic) =>
            well?.Items.FirstOrDefault(item => item.Mnemonic.Equals(mnemonic, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        List<string> wellParts = [];
        if (Value("WELL") is { Length: > 0 } wellName)
        {
            wellParts.Add("Скважина " + wellName);
        }

        foreach (string mnemonic in new[] { "FLD", "COMP", "SRVC", "DATE" })
        {
            if (Value(mnemonic) is { Length: > 0 } value)
            {
                wellParts.Add(value);
            }
        }

        string first = wellParts.Count > 0 ? string.Join(" · ", wellParts) : document.Document.Name;
        string unit = target.Sources.IndexUnit;
        string what = target.Content is CombinedTablet combined
            ? $"{combined.Name} ({string.Join(", ", combined.Sources.Select(source => source.Document.Document.Name))})"
            : document.Document.Name;
        string second = $"{what} · {options.Top.ToString("0.##", culture)} – {options.Bottom.ToString("0.##", culture)} {unit}"
                        + $" · масштаб 1:{Math.Round(options.Ratio).ToString("N0", culture)}";
        return [first, second, footer];
    }
}
