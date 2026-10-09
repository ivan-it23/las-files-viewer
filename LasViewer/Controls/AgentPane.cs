using System.Globalization;
using System.Text;
using System.Text.Json;
using AMK.LasViewer.Agent;
using AMK.LasViewer.App.Dialogs;
using AMK.LasViewer.App.Services;
using AMK.LasViewer.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Чат с ИИ-агентом. Вопрос уходит через посредника на сайте компании,
/// инструменты выполняет движок на этой машине: модель видит только их
/// результаты, а не файлы целиком.
/// </summary>
/// <remarks>
/// Ответ модели приходит не из потока интерфейса. Все изменения элементов
/// проходят через <see cref="Post"/>: очередь окна и перехват исключений —
/// сбой отрисовки ответа не должен закрывать программу.
/// </remarks>
public sealed partial class AgentPane : UserControl
{
    private static readonly SolidColorBrush UserBubble = new(Color.FromArgb(0xFF, 0xE3, 0xEE, 0xFB));
    private static readonly SolidColorBrush ToolForeground = new(Color.FromArgb(0xFF, 0x6B, 0x6B, 0x6B));
    private static readonly SolidColorBrush ErrorForeground = new(Color.FromArgb(0xFF, 0xB0, 0x1C, 0x28));
    private static readonly SolidColorBrush MutedForeground = new(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));
    private static readonly SolidColorBrush HeaderBackground = new(Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
    private static readonly SolidColorBrush LineBrush = new(Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));

    private static readonly string[] Examples =
    [
        "Опиши открытые файлы: интервал, кривые, замечания",
        "Где УЭС стоит у предела преобразования?",
        "Найди пропуски данных во всех кривых",
        "Сравни зонды 400 и 2000 кГц на видимом участке"
    ];

    /// <summary>
    /// Настройки агента. LASVIEWER_AGENT_SETTINGS подменяет файл — для
    /// проверки с учебным посредником, не трогая настройки пользователя.
    /// </summary>
    private readonly AgentSettingsStore store = new(
        Environment.GetEnvironmentVariable("LASVIEWER_AGENT_SETTINGS") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(AppInfo.DataDirectory, "agent-settings.json"));
    private readonly AgentSession session = new();
    private readonly StackPanel messages = new() { Spacing = 14, Padding = new Thickness(12, 12, 12, 16) };
    private readonly ScrollViewer scroller;
    private readonly TextBox input;
    private readonly Button sendButton;
    private readonly FontIcon sendIcon = new() { Glyph = "", FontSize = 14 };
    private readonly TextBlock subtitle = new() { FontSize = 11, Foreground = MutedForeground, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock footer = new() { FontSize = 11, Foreground = MutedForeground, TextWrapping = TextWrapping.Wrap };

    private AgentSettings settings;
    private CancellationTokenSource? running;

    public AgentPane()
    {
        settings = store.Load();

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Заголовок: название, подключение, новый диалог, настройки.
        Grid header = new() { Background = HeaderBackground, Padding = new Thickness(12, 6, 6, 6), ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel title = new() { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Агент", FontWeight = FontWeights.SemiBold });
        title.Children.Add(subtitle);
        header.Children.Add(title);
        Button newChat = IconButton("", "Новый диалог");
        newChat.Click += (_, _) => NewConversation();
        Grid.SetColumn(newChat, 1);
        header.Children.Add(newChat);
        Button settingsButton = IconButton("", "Подключение к ИИ-агенту");
        settingsButton.Click += async (_, _) => await EditSettingsAsync();
        Grid.SetColumn(settingsButton, 2);
        header.Children.Add(settingsButton);
        root.Children.Add(header);

        scroller = new ScrollViewer
        {
            Content = messages,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        // Ввод вопроса: Enter — отправить, Shift + Enter — новая строка.
        input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160,
            MinHeight = 36,
            PlaceholderText = "Вопрос о данных открытых файлов…",
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Вопрос агенту");
        input.PreviewKeyDown += Input_PreviewKeyDown;
        sendButton = new Button
        {
            Content = sendIcon,
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, 8, 10, 8),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        ToolTipService.SetToolTip(sendButton, "Отправить (Enter)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sendButton, "Отправить или остановить");
        sendButton.Click += (_, _) => SendOrStop();

        Grid inputRow = new() { ColumnSpacing = 6 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(input);
        Grid.SetColumn(sendButton, 1);
        inputRow.Children.Add(sendButton);

        StackPanel bottom = new() { Spacing = 4 };
        bottom.Children.Add(inputRow);
        bottom.Children.Add(footer);
        Border bottomBorder = new()
        {
            Child = bottom,
            Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        Grid.SetRow(bottomBorder, 2);
        root.Children.Add(bottomBorder);

        Content = root;
        UpdateHeader();
        UpdateFooter();
        ShowWelcome();
    }

    internal DialogService? Dialogs { get; set; }

    internal EngineHost? Engine { get; set; }

    /// <summary>Описание того, что сейчас открыто и видно в программе, — для системного сообщения.</summary>
    internal Func<string>? ContextProvider { get; set; }

    private bool IsRunning => running is not null;

    // ------------------------------------------------------------------
    // Диалог
    // ------------------------------------------------------------------

    private void Input_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!shift)
        {
            e.Handled = true;
            SendOrStop();
        }
    }

    private void SendOrStop()
    {
        if (IsRunning)
        {
            running!.Cancel();
            return;
        }

        string question = input.Text.Trim();
        if (question.Length > 0)
        {
            _ = AskAsync(question);
        }
    }

    private async Task AskAsync(string question)
    {
        if (!settings.IsConfigured && !await EditSettingsAsync())
        {
            return;
        }

        if (Engine is not { State: EngineState.Ready } engine)
        {
            AddError("Движок не готов. Дождитесь запуска или перезапустите его в меню «Движок».");
            return;
        }

        input.Text = string.Empty;
        RemoveWelcome();
        AddUserMessage(question);
        TurnView turn = new(this);
        messages.Children.Add(turn.Root);
        ScrollToEnd();

        using CancellationTokenSource cancellation = new();
        running = cancellation;
        sendIcon.Glyph = "";
        ToolTipService.SetToolTip(sendButton, "Остановить");

        string context = BuildContext();
        AppLog.Write($"Агент: вопрос ({question.Length} симв.), модель {(settings.Model.Length > 0 ? settings.Model : "по умолчанию")}");
        try
        {
            using LlmProxyClient client = new(settings.ServerUrl, settings.Token);
            string answer = await session.AskAsync(
                question,
                context,
                client,
                settings.Model.Length > 0 ? settings.Model : null,
                new EngineAgentTools(engine),
                new TurnObserver(this, turn),
                cancellation.Token);
            // Последние фрагменты текста ещё могут стоять в очереди окна —
            // завершение ставится за ними.
            Post(() => turn.Finish(answer, null));
        }
        catch (OperationCanceledException)
        {
            Post(() => turn.Finish(null, "Остановлено. Этот вопрос не сохранён в истории диалога."));
        }
        catch (LlmProxyException exception)
        {
            AppLog.Write($"Агент: ошибка {exception.Code}: {exception.Message}");
            bool fixable = exception.IsAuthorizationProblem || exception.Code is "not_deployed" or "model_not_allowed";
            Post(() => turn.Fail(exception.Message, fixable));
        }
        catch (Exception exception) when (exception is EngineException or ArgumentException or IOException or JsonException)
        {
            AppLog.Write($"Агент: сбой: {exception}");
            Post(() => turn.Fail(exception.Message, false));
        }
        finally
        {
            running = null;
            sendIcon.Glyph = "";
            ToolTipService.SetToolTip(sendButton, "Отправить (Enter)");
            UpdateFooter();
        }
    }

    private void NewConversation()
    {
        running?.Cancel();
        session.Reset();
        messages.Children.Clear();
        ShowWelcome();
        UpdateFooter();
    }

    private async Task<bool> EditSettingsAsync()
    {
        if (Dialogs is null)
        {
            return false;
        }

        AgentSettings? updated = await AgentSettingsDialog.ShowAsync(Dialogs, settings);
        if (updated is null)
        {
            return settings.IsConfigured;
        }

        settings = updated;
        if (!store.TrySave(settings))
        {
            await Dialogs.ShowMessageAsync("Настройки не сохранены", $"Не удалось записать {store.SettingsPath}.");
        }

        UpdateHeader();
        if (messages.Children.Count == 1 && messages.Children[0] is FrameworkElement { Tag: "welcome" })
        {
            ShowWelcome();
        }

        return settings.IsConfigured;
    }

    private string BuildContext()
    {
        try
        {
            return ContextProvider?.Invoke() ?? string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            AppLog.Write($"Агент: контекст не собран: {exception.Message}");
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Элементы чата
    // ------------------------------------------------------------------

    private void ShowWelcome()
    {
        messages.Children.Clear();
        StackPanel welcome = new() { Spacing = 8, Tag = "welcome" };
        welcome.Children.Add(new TextBlock
        {
            Text = "Агент отвечает на вопросы о данных открытых файлов: считает статистику, ищет интервалы, сравнивает кривые. "
                   + "Файлы целиком никуда не отправляются — модель получает только результаты расчётов, выполненных на этом компьютере.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedForeground,
            FontSize = 13
        });

        if (!settings.IsConfigured)
        {
            Button connect = new() { Content = "Подключить агента…" };
            connect.Click += async (_, _) => await EditSettingsAsync();
            welcome.Children.Add(connect);
        }
        else
        {
            welcome.Children.Add(new TextBlock { Text = "Например:", FontSize = 12, Foreground = MutedForeground, Margin = new Thickness(0, 4, 0, 0) });
            foreach (string example in Examples)
            {
                HyperlinkButton link = new() { Content = new TextBlock { Text = example, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(0, 2, 0, 2) };
                link.Click += (_, _) =>
                {
                    input.Text = example;
                    input.Focus(FocusState.Programmatic);
                    input.SelectionStart = example.Length;
                };
                welcome.Children.Add(link);
            }
        }

        messages.Children.Add(welcome);
    }

    private void RemoveWelcome()
    {
        if (messages.Children.Count > 0 && messages.Children[0] is FrameworkElement { Tag: "welcome" } welcome)
        {
            messages.Children.Remove(welcome);
        }
    }

    private void AddUserMessage(string text)
    {
        messages.Children.Add(new Border
        {
            Background = UserBubble,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 0, 0, 0),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 }
        });
    }

    private void AddError(string text)
    {
        RemoveWelcome();
        messages.Children.Add(new TextBlock { Text = text, Foreground = ErrorForeground, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        ScrollToEnd();
    }

    private void UpdateHeader()
    {
        if (!settings.IsConfigured)
        {
            subtitle.Text = "Не подключён";
            return;
        }

        string host;
        try
        {
            host = new Uri(LlmProxyClient.BuildEndpoint(settings.ServerUrl)).Host;
        }
        catch (ArgumentException)
        {
            // Файл настроек правили вручную: адрес покажется как есть, а
            // ошибку объяснит первый же запрос.
            host = settings.ServerUrl;
        }

        subtitle.Text = (settings.Model.Length > 0 ? settings.Model : "модель по умолчанию") + " · " + host;
    }

    private void UpdateFooter()
    {
        string spent = session.SpentRub > 0
            ? $"Расход диалога: {session.SpentRub.ToString("0.##", CultureInfo.CurrentCulture)} ₽ · "
            : string.Empty;
        footer.Text = spent + "Enter — отправить, Shift + Enter — новая строка";
    }

    private void ScrollToEnd()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true));
    }

    /// <summary>Выполнить в потоке интерфейса; исключение пишется в журнал, а не закрывает программу.</summary>
    private void Post(Action action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                AppLog.Write($"Агент: сбой отображения ответа: {exception}");
            }
        });
    }

    private static Button IconButton(string glyph, string tooltip)
    {
        Button button = new()
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6)
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    /// <summary>Подпись вызова инструмента для пользователя.</summary>
    private static string Describe(ToolCall call)
    {
        JsonElement args = default;
        try
        {
            args = JsonDocument.Parse(call.Arguments).RootElement;
        }
        catch (JsonException)
        {
        }

        string Text(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetDouble().ToString("0.##", CultureInfo.CurrentCulture),
                JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(item => item.ToString())),
                _ => string.Empty
            }
            : string.Empty;

        string range = Text("top") is { Length: > 0 } top || Text("bottom").Length > 0
            ? $" · {Text("top")}–{Text("bottom")}"
            : string.Empty;

        return call.Name switch
        {
            "list_files" => "Список открытых файлов",
            "get_file_info" => "Сведения о файле",
            "curve_stats" => $"Статистика: {Text("curves")}{range}",
            "find_intervals" => $"Поиск интервалов: {Text("curve")} {Condition(Text("condition"))} {Text("value")}{(Text("value2") is { Length: > 0 } v2 ? "…" + v2 : string.Empty)}{range}".TrimEnd(),
            "get_samples" => $"Значения кривых: {Text("curves")}{range}",
            "compare_curves" => $"Сравнение: {Text("curve")} и {Text("other_curve")}{range}",
            _ => call.Name
        };

        static string Condition(string condition) => condition switch
        {
            "gt" => ">",
            "ge" => "≥",
            "lt" => "<",
            "le" => "≤",
            "between" => "в диапазоне",
            "outside" => "вне диапазона",
            "null" => "— пропуски",
            "plateau" => "— «полки»",
            _ => condition
        };
    }

    // ------------------------------------------------------------------
    // Ответ на один вопрос
    // ------------------------------------------------------------------

    /// <summary>Блок ответа: текст модели и строки вызовов инструментов в порядке поступления.</summary>
    private sealed class TurnView(AgentPane pane)
    {
        private readonly Dictionary<string, TextBlock> toolLines = [];
        private readonly StringBuilder text = new();
        private TextBlock? streaming;
        private bool shownAnyText;

        public StackPanel Root { get; } = new() { Spacing = 6 };

        public void AppendText(string piece)
        {
            if (streaming is null)
            {
                streaming = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, IsTextSelectionEnabled = true };
                Root.Children.Add(streaming);
            }

            text.Append(piece);
            streaming.Text = text.ToString();
            shownAnyText = true;
            pane.ScrollToEnd();
        }

        public void ToolStarted(ToolCall call)
        {
            CompleteText();
            TextBlock line = new()
            {
                Text = "⚙ " + Describe(call),
                FontSize = 12,
                Foreground = ToolForeground,
                TextWrapping = TextWrapping.Wrap
            };
            toolLines[call.Id] = line;
            Root.Children.Add(line);
            pane.ScrollToEnd();
        }

        public void ToolFinished(ToolCall call, bool ok, string error)
        {
            if (toolLines.TryGetValue(call.Id, out TextBlock? line))
            {
                line.Text = (ok ? "✓ " : "✗ ") + Describe(call) + (ok || error.Length == 0 ? string.Empty : " — " + error);
            }
        }

        public void Finish(string? answer, string? note)
        {
            CompleteText();
            if (!shownAnyText && !string.IsNullOrEmpty(answer))
            {
                Root.Children.Add(MarkdownView.Render(answer));
            }

            if (note is not null)
            {
                Root.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = MutedForeground, TextWrapping = TextWrapping.Wrap });
            }

            pane.ScrollToEnd();
        }

        public void Fail(string message, bool offerSettings)
        {
            CompleteText();
            Root.Children.Add(new TextBlock { Text = message, Foreground = ErrorForeground, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            if (offerSettings)
            {
                Button fix = new() { Content = "Настроить подключение…" };
                fix.Click += async (_, _) => await pane.EditSettingsAsync();
                Root.Children.Add(fix);
            }

            pane.ScrollToEnd();
        }

        /// <summary>Набранный потоком текст заменяется размеченным, когда кусок ответа закончен.</summary>
        private void CompleteText()
        {
            if (streaming is null)
            {
                return;
            }

            int position = Root.Children.IndexOf(streaming);
            string completed = text.ToString().Trim();
            Root.Children.RemoveAt(position);
            if (completed.Length > 0)
            {
                Root.Children.Insert(position, MarkdownView.Render(completed));
            }

            streaming = null;
            text.Clear();
        }
    }

    private sealed class TurnObserver(AgentPane pane, TurnView turn) : IAgentObserver
    {
        public void TextDelta(string text) => pane.Post(() => turn.AppendText(text));

        public void ToolStarted(ToolCall call) => pane.Post(() => turn.ToolStarted(call));

        public void ToolFinished(ToolCall call, bool ok, string summary) => pane.Post(() => turn.ToolFinished(call, ok, summary));

        public void StepFinished(ChatUsage? usage) => pane.Post(pane.UpdateFooter);
    }
}
