using AMK.LasViewer.Agent;
using AMK.LasViewer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>
/// Вопрос перед запуском скрипта агента: пользователь видит код целиком,
/// данные каких файлов он получит, и решает, запускать ли его.
/// </summary>
internal static class ScriptApprovalDialog
{
    /// <returns>
    /// <c>Run</c> — запускать; <c>TrustConversation</c> — не спрашивать до конца диалога.
    /// Отмена <paramref name="cancellationToken"/> закрывает окно и бросает <see cref="OperationCanceledException"/>.
    /// </returns>
    public static async Task<(bool Run, bool TrustConversation)> ShowAsync(
        DialogService dialogs, ScriptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TextBox code = new()
        {
            Text = request.Code,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 160,
            MaxHeight = 420,
            MinWidth = 560
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(code, "Код скрипта");

        CheckBox trust = new() { Content = "Запускать скрипты без вопроса до конца этого диалога с агентом" };

        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = request.Title.Length > 0
                ? $"Агент хочет выполнить на этом компьютере скрипт Python: «{request.Title}»."
                : "Агент хочет выполнить на этом компьютере скрипт Python.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Данные файлов: " + string.Join(", ", request.FileNames),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        });
        content.Children.Add(code);
        content.Children.Add(new TextBlock
        {
            Text = $"Скрипт работает в отдельном процессе не дольше {request.TimeoutSeconds} с, без доступа к сети "
                   + "и без права записи вне своей папки; LAS-файлы он не изменяет. Код, графики и таблицы сохраняются в "
                   + AppInfo.ScriptsDirectory + ".",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        });
        content.Children.Add(trust);

        ContentDialog dialog = new()
        {
            Title = "Запустить скрипт агента?",
            Content = content,
            PrimaryButtonText = "Запустить",
            CloseButtonText = "Отклонить",
            // Enter не должен запускать код, который пользователь не прочитал.
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;

        // «Стоп» в панели агента, пока окно открыто, — окно закрывается.
        using CancellationTokenRegistration hide = cancellationToken.Register(
            () => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        ContentDialogResult result = await dialogs.ShowAsync(dialog);
        cancellationToken.ThrowIfCancellationRequested();

        bool run = result == ContentDialogResult.Primary;
        return (run, run && trust.IsChecked == true);
    }
}
