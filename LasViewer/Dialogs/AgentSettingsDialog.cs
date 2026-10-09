using System.Globalization;
using AMK.LasViewer.Agent;
using AMK.LasViewer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>
/// Подключение к ИИ-агенту: адрес посредника и модель. Менять ничего не нужно —
/// по умолчанию работают посредник на сайте компании и модель сайта.
/// </summary>
internal static class AgentSettingsDialog
{
    /// <returns>Новые настройки или <c>null</c>, если пользователь отказался.</returns>
    public static async Task<AgentSettings?> ShowAsync(DialogService dialogs, AgentSettings current)
    {
        TextBox server = new() { Header = "Адрес посредника", Text = current.ServerUrl, MinWidth = 420 };
        TextBlock serverHint = new()
        {
            Text = "Посредник на сайте компании подставляет ключ polza.ai и модель — те же, что у ИИ-агента horizon.ru. "
                   + "Менять адрес нужно, только если посредник переехал.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12
        };

        ComboBox model = new() { Header = "Модель", MinWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
        model.Items.Add(new ComboBoxItem { Content = "Модель сайта (как у ИИ-агента horizon.ru)", Tag = string.Empty });
        if (current.Model.Length > 0)
        {
            model.Items.Add(new ComboBoxItem { Content = current.Model, Tag = current.Model });
        }

        model.SelectedIndex = current.Model.Length > 0 ? 1 : 0;

        Button check = new() { Content = "Проверить подключение" };
        ProgressRing progress = new() { IsActive = false, Width = 18, Height = 18 };
        TextBlock result = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        StackPanel checkRow = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        checkRow.Children.Add(check);
        checkRow.Children.Add(progress);

        StackPanel content = new() { Spacing = 12, MinWidth = 440 };
        content.Children.Add(server);
        content.Children.Add(serverHint);
        content.Children.Add(checkRow);
        content.Children.Add(result);
        content.Children.Add(model);

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            progress.IsActive = true;
            result.Text = string.Empty;
            try
            {
                using LlmProxyClient client = new(server.Text);
                ProxyStatus status = await client.GetStatusAsync(CancellationToken.None);
                (string defaultModel, IReadOnlyList<ModelInfo> models) = await client.GetModelsAsync(CancellationToken.None);

                CultureInfo culture = CultureInfo.CurrentCulture;
                result.Text = "Посредник работает.\n"
                              + $"Сегодня израсходовано {status.SpentTodayRub.ToString("0.##", culture)} из {status.DailyBudgetRub.ToString("0.##", culture)} ₽, "
                              + $"запросов за час — {status.RequestsLastHour} из {status.RequestsPerHour}."
                              + (status.Configured ? string.Empty : "\nНа сайте не задан ключ polza.ai — агент работать не будет.");

                string selected = model.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;
                model.Items.Clear();
                model.Items.Add(new ComboBoxItem { Content = $"Модель сайта ({defaultModel})", Tag = string.Empty });
                foreach (ModelInfo info in models)
                {
                    string price = info.PromptPerMillion is { } prompt && info.CompletionPerMillion is { } completion
                        ? $" — {prompt.ToString("0.##", culture)} / {completion.ToString("0.##", culture)} ₽ за 1 млн токенов"
                        : string.Empty;
                    model.Items.Add(new ComboBoxItem { Content = info.Name + price, Tag = info.Id });
                }

                model.SelectedItem = model.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected)
                                     ?? model.Items[0];
            }
            catch (Exception exception) when (exception is LlmProxyException or ArgumentException)
            {
                result.Text = exception.Message;
            }
            finally
            {
                progress.IsActive = false;
                check.IsEnabled = true;
            }
        };

        ContentDialog dialog = new()
        {
            Title = "Подключение к ИИ-агенту (необязательно)",
            Content = new ScrollViewer { Content = content, MaxHeight = 560 },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        AgentSettings? saved = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                LlmProxyClient.BuildEndpoint(server.Text);
            }
            catch (ArgumentException exception)
            {
                result.Text = exception.Message;
                args.Cancel = true;
                return;
            }

            saved = current.Clone();
            saved.ServerUrl = server.Text.Trim();
            saved.Token = string.Empty;
            saved.Model = model.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;
        };

        await dialogs.ShowAsync(dialog);
        return saved;
    }
}
