using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using WinRT.Interop;

namespace AMK.LasViewer.App.Services;

/// <summary>
/// Печать через системный диалог Windows. PDF получается выбором принтера
/// «Microsoft Print to PDF».
/// </summary>
/// <remarks>
/// Страницы строятся заново при каждом пересчёте: пользователь меняет в
/// диалоге бумагу или ориентацию, и планшет раскладывается под новую область
/// печати.
/// </remarks>
internal sealed class TabletPrinter(Window owner)
{
    private PrintManager? manager;
    private PrintDocument? document;
    private IPrintDocumentSource? source;
    private Func<PrintPageDescription, IReadOnlyList<UIElement>>? buildPages;
    private IReadOnlyList<UIElement> pages = [];
    private string jobTitle = string.Empty;

    public async Task PrintAsync(string title, Func<PrintPageDescription, IReadOnlyList<UIElement>> pageBuilder)
    {
        IntPtr window = WindowNative.GetWindowHandle(owner);
        if (manager is null)
        {
            manager = PrintManagerInterop.GetForWindow(window);
            manager.PrintTaskRequested += OnPrintTaskRequested;
        }

        jobTitle = title;
        buildPages = pageBuilder;
        document = new PrintDocument();
        source = document.DocumentSource;
        document.Paginate += OnPaginate;
        document.GetPreviewPage += OnGetPreviewPage;
        document.AddPages += OnAddPages;

        await PrintManagerInterop.ShowPrintUIForWindowAsync(window);
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        PrintTask task = args.Request.CreatePrintTask(jobTitle, request => request.SetSource(source));
        task.Completed += (_, completed) => AppLog.Write($"Печать «{jobTitle}»: {completed.Completion}");
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        PrintPageDescription description = e.PrintTaskOptions.GetPageDescription(0);
        pages = buildPages?.Invoke(description) ?? [];
        document?.SetPreviewPageCount(pages.Count, PreviewPageCountType.Final);
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (e.PageNumber >= 1 && e.PageNumber <= pages.Count)
        {
            document?.SetPreviewPage(e.PageNumber, pages[e.PageNumber - 1]);
        }
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        foreach (UIElement page in pages)
        {
            document?.AddPage(page);
        }

        document?.AddPagesComplete();
    }
}
