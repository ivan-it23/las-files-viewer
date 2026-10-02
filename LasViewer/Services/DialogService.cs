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

    /// <summary>Строка от пользователя; <c>null</c> — отмена.</summary>
    public async Task<string?> PromptAsync(string title, string label, string initial, string confirmText)
    {
        TextBox box = new() { Text = initial, Header = label, MinWidth = 360 };
        box.SelectAll();
        ContentDialog dialog = new()
        {
            Title = title,
            Content = box,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = box.Text.Trim().Length == 0;
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>Вопрос с тремя ответами: да, нет, отмена.</summary>
    public Task<ContentDialogResult> AskAsync(string title, string message, string yes, string no)
    {
        return ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            SecondaryButtonText = no,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
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
