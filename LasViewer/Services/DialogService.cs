using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App.Services;

/// <summary>
/// Единственный путь показа <see cref="ContentDialog"/>.
/// </summary>
/// <remarks>
/// WinUI допускает только один открытый диалог на окно: второй <c>ShowAsync</c>
/// падает исключением, а из <c>async void</c> обработчика его перехватить
/// некому. Поэтому диалоги показываются по очереди.
/// </remarks>
internal sealed class DialogService(Func<XamlRoot?> xamlRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await gate.WaitAsync();
        try
        {
            dialog.XamlRoot = xamlRoot() ?? throw new InvalidOperationException("Окно ещё не показано.");
            return await dialog.ShowAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task ShowMessageAsync(string title, string message)
    {
        return ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "ОК"
        });
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText, bool confirmIsDefault)
    {
        ContentDialogResult result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = confirmIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close
        });
        return result == ContentDialogResult.Primary;
    }
}
