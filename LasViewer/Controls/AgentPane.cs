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
/// Чат с ИИ-агентом. Вопрос уходит модели через посредника компании,
/// инструменты выполняет движок на этой машине: модель видит только их
/// результаты, а не файлы целиком. Пользователь ничего не настраивает —
/// только выбирает модель.
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
        "Проанализируй открытые файлы и опиши их",
        "На каких участках УЭС уходит в зашкал вверх или вниз?",
        "На каких участках разность фаз отрицательная?",
        "Разбей разрез на пропластки, где УЭС растёт и где убывает с номером зонда, и построй график",
        "Составь отчёт в Word о зашкалах УЭС: таблица интервалов, график и краткий вывод"
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
    private readonly TextBlock footer = new() { FontSize = 11, Foreground = MutedForeground, TextWrapping = TextWrapping.Wrap };
    private readonly Button modelButton = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        FontSize = 12,
        MinHeight = 28,
        Padding = new Thickness(8, 2, 8, 2)
    };
    private readonly TextBlock modelTitle = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private AgentSettings settings;
    private CancellationTokenSource? running;

    /// <summary>
    /// Пользователь разрешил запускать скрипты без вопроса до конца диалога.
    /// Читается из потока агента, поэтому volatile; сбрасывается новым диалогом.
    /// </summary>
    private volatile bool runScriptsWithoutAsking;

    /// <summary>Пользователь нажал «Свернуть панель агента»; сворачивает её окно, которому принадлежит колонка.</summary>
    public event EventHandler? HideRequested;

    public AgentPane()
    {
        settings = store.Load();

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Заголовок: название, новый диалог и сворачивание панели.
        Grid header = new() { Background = HeaderBackground, Padding = new Thickness(12, 6, 6, 6), ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Агент", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Button newChat = IconButton("", "Новый диалог");
        newChat.Click += (_, _) => NewConversation();
        Grid.SetColumn(newChat, 1);
        header.Children.Add(newChat);
        Button hide = IconButton("", "Свернуть панель агента");
        hide.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(hide, 2);
        header.Children.Add(hide);
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

        Grid modelRow = new() { ColumnSpacing = 6 };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.Children.Add(new TextBlock { Text = "Модель", FontSize = 12, Foreground = MutedForeground, VerticalAlignment = VerticalAlignment.Center });
        Grid modelContent = new() { ColumnSpacing = 6 };
        modelContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelContent.Children.Add(modelTitle);
        FontIcon chevron = new() { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        modelContent.Children.Add(chevron);
        modelButton.Content = modelContent;
        modelButton.Click += (_, _) => ShowModelChoice();
        Grid.SetColumn(modelButton, 1);
        modelRow.Children.Add(modelButton);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(modelButton, "Модель агента");
        UpdateModelTitle();

        StackPanel bottom = new() { Spacing = 6 };
        bottom.Children.Add(modelRow);
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
        modelButton.IsEnabled = false;
        ToolTipService.SetToolTip(sendButton, "Остановить");

        string context = BuildContext();
        AppLog.Write($"Агент: вопрос ({question.Length} симв.), модель {settings.Model}");
        try
        {
            using LlmProxyClient client = new(settings.ServerUrl);
            string answer = await session.AskAsync(
                question,
                context,
                client,
                settings.Model,
                new EngineAgentTools(engine, new ScriptApprover(this), AppInfo.ScriptsDirectory),
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
            Post(() => turn.Fail(exception.Message));
        }
        catch (Exception exception) when (exception is EngineException or ArgumentException or IOException or JsonException)
        {
            AppLog.Write($"Агент: сбой: {exception}");
            Post(() => turn.Fail(exception.Message));
        }
        finally
        {
            running = null;
            sendIcon.Glyph = "";
            modelButton.IsEnabled = true;
            ToolTipService.SetToolTip(sendButton, "Отправить (Enter)");
            UpdateFooter();
        }
    }

    private void NewConversation()
    {
        running?.Cancel();
        session.Reset();
        runScriptsWithoutAsking = false;
        messages.Children.Clear();
        ShowWelcome();
        UpdateFooter();
    }

    /// <summary>Выбор модели: два раздела — большие и малые языковые модели.</summary>
    private void ShowModelChoice()
    {
        Flyout flyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        StackPanel list = new() { Spacing = 2, Width = 240 };
        foreach ((bool large, string header) in new[] { (true, "Большие языковые модели:"), (false, "Малые языковые модели:") })
        {
            list.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedForeground,
                Margin = new Thickness(8, list.Children.Count == 0 ? 0 : 10, 0, 2)
            });

            foreach (AgentModel model in AgentSettings.Models.Where(model => model.Large == large))
            {
                list.Children.Add(ModelRow(model, flyout));
            }
        }

        flyout.Content = list;
        flyout.ShowAt(modelButton);
    }

    private Button ModelRow(AgentModel model, Flyout flyout)
    {
        bool selected = model.Id == settings.Model;
        Grid row = new() { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (selected)
        {
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        }

        TextBlock text = new() { Text = model.Title, FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        Button button = new()
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, model.Title);
        button.Click += (_, _) =>
        {
            flyout.Hide();
            SelectModel(model);
        };
        return button;
    }

    private void SelectModel(AgentModel model)
    {
        if (model.Id == settings.Model)
        {
            return;
        }

        settings.Model = model.Id;
        if (!store.TrySave(settings))
        {
            AppLog.Write($"Агент: выбор модели не сохранён в {store.SettingsPath}");
        }

        UpdateModelTitle();
        AppLog.Write($"Агент: выбрана модель {model.Id}");
    }

    private void UpdateModelTitle()
    {
        AgentModel model = AgentSettings.ResolveModel(settings.Model);
        modelTitle.Text = model.Title;
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
            Text = "Агент отвечает на вопросы о данных открытых файлов: считает статистику, ищет интервалы, сравнивает кривые, "
                   + "строит графики скриптами Python — каждый скрипт запускается только после вашего подтверждения. "
                   + "Файлы целиком никуда не отправляются — модель получает только результаты расчётов, выполненных на этом компьютере.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedForeground,
            FontSize = 13
        });

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
            "find_intervals" => $"Поиск интервалов: {(Text("curve") is { Length: > 0 } one ? one : Text("curves"))} {Condition(Text("condition"))} {Text("value")}{(Text("value2") is { Length: > 0 } v2 ? "…" + v2 : string.Empty)}{range}".TrimEnd(),
            "get_samples" => $"Значения кривых: {Text("curves")}{range}",
            "compare_curves" => $"Сравнение: {Text("curve")} и {Text("other_curve")}{range}",
            EngineAgentTools.ScriptToolName => Text("title") is { Length: > 0 } title ? $"Скрипт: {title}" : "Скрипт Python",
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

    /// <summary>
    /// Блок ответа: текст модели и строки вызовов инструментов в порядке поступления.
    /// </summary>
    /// <remarks>
    /// Текст размечается сразу, по мере поступления. Законченные блоки —
    /// всё до последней пустой строки вне блока кода — размечаются один раз и
    /// больше не перерисовываются; перерисовывается только последний, ещё
    /// растущий блок, и не чаще чем раз в <see cref="RenderDelay"/>: на
    /// длинном ответе перерисовка всего текста на каждый фрагмент тормозила бы.
    /// </remarks>
    private sealed class TurnView
    {
        private static readonly TimeSpan RenderDelay = TimeSpan.FromMilliseconds(90);

        private readonly AgentPane pane;
        private readonly Dictionary<string, TextBlock> toolLines = [];
        private readonly StringBuilder text = new();
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer renderTimer;

        /// <summary>Текущий кусок текста — между вызовами инструментов.</summary>
        private StackPanel? segment;

        /// <summary>Сколько символов <see cref="text"/> уже размечено окончательно.</summary>
        private int stableLength;

        private FrameworkElement? tail;
        private bool shownAnyText;

        public TurnView(AgentPane pane)
        {
            this.pane = pane;
            renderTimer = pane.DispatcherQueue.CreateTimer();
            renderTimer.Interval = RenderDelay;
            renderTimer.IsRepeating = false;
            renderTimer.Tick += (_, _) =>
            {
                try
                {
                    RenderSegment(final: false);
                }
                catch (Exception exception)
                {
                    AppLog.Write($"Агент: сбой разметки ответа: {exception}");
                }
            };
        }

        public StackPanel Root { get; } = new() { Spacing = 6 };

        public void AppendText(string piece)
        {
            if (segment is null)
            {
                segment = new StackPanel { Spacing = 6 };
                Root.Children.Add(segment);
            }

            text.Append(piece);
            shownAnyText = true;
            if (!renderTimer.IsRunning)
            {
                renderTimer.Start();
            }
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

        public void ToolFinished(ToolCall call, ToolResult result)
        {
            bool script = call.Name == EngineAgentTools.ScriptToolName;
            string error = result.Error ?? string.Empty;
            if (script && error == EngineAgentTools.ScriptRejected)
            {
                error = "отклонён";
            }

            if (toolLines.TryGetValue(call.Id, out TextBlock? line))
            {
                line.Text = (result.Ok ? "✓ " : "✗ ") + Describe(call) + (result.Ok || error.Length == 0 ? string.Empty : " — " + error);
            }

            if (script && error != "отклонён" && ScriptResultView.Build(call, result) is { } view)
            {
                Root.Children.Add(view);
                pane.ScrollToEnd();
            }
        }

        public void Notice(string message)
        {
            CompleteText();
            Root.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = ToolForeground, TextWrapping = TextWrapping.Wrap });
            pane.ScrollToEnd();
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

        public void Fail(string message)
        {
            CompleteText();
            Root.Children.Add(new TextBlock { Text = message, Foreground = ErrorForeground, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            pane.ScrollToEnd();
        }

        /// <summary>Кусок ответа закончен (вызов инструмента или конец ответа) — размечается целиком.</summary>
        private void CompleteText()
        {
            if (segment is null)
            {
                return;
            }

            renderTimer.Stop();
            RenderSegment(final: true);
            if (segment.Children.Count == 0)
            {
                Root.Children.Remove(segment);
            }

            segment = null;
            tail = null;
            stableLength = 0;
            text.Clear();
        }

        private void RenderSegment(bool final)
        {
            if (segment is null)
            {
                return;
            }

            string all = text.ToString();
            int boundary = final ? all.Length : StableBoundary(all);
            if (boundary > stableLength)
            {
                string stable = all[stableLength..boundary].Trim();
                if (stable.Length > 0)
                {
                    int position = tail is null ? segment.Children.Count : segment.Children.IndexOf(tail);
                    segment.Children.Insert(position, MarkdownView.Render(stable));
                }

                stableLength = boundary;
            }

            if (tail is not null)
            {
                segment.Children.Remove(tail);
                tail = null;
            }

            string rest = all[stableLength..].Trim();
            if (rest.Length > 0)
            {
                tail = MarkdownView.Render(CloseOpenMarkup(rest));
                segment.Children.Add(tail);
            }

            pane.ScrollToEnd();
        }

        /// <summary>
        /// Конец последнего законченного блока: последняя пустая строка вне
        /// блока кода. Текст после неё ещё может измениться — например,
        /// строка таблицы или пункт списка, пришедшие наполовину.
        /// </summary>
        private static int StableBoundary(string all)
        {
            int boundary = 0;
            bool inCode = false;
            int lineStart = 0;
            while (lineStart < all.Length)
            {
                int lineEnd = all.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    break; // последняя строка не закончена
                }

                string line = all[lineStart..lineEnd].Trim();
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                }
                else if (line.Length == 0 && !inCode)
                {
                    boundary = lineEnd + 1;
                }

                lineStart = lineEnd + 1;
            }

            return boundary;
        }

        /// <summary>
        /// Для растущего блока закрывает начатую разметку — жирный, код в
        /// строке, блок кода, — чтобы пользователь не видел «**» и «`», пока
        /// модель не допишет вторую половину.
        /// </summary>
        private static string CloseOpenMarkup(string block)
        {
            int fences = 0;
            foreach (string line in block.Split('\n'))
            {
                if (line.Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    fences++;
                }
            }

            if (fences % 2 == 1)
            {
                return block + "\n```";
            }

            string lastLine = block[(block.LastIndexOf('\n') + 1)..];
            if (CountOccurrences(lastLine, "**") % 2 == 1)
            {
                // Нечётное число «**» — последний открывает выделение. Если за
                // ним ещё ничего нет, он убирается, иначе выделение закрывается.
                return block.EndsWith("**", StringComparison.Ordinal) ? block[..^2] : block + "**";
            }

            if (CountOccurrences(lastLine.Replace("**", string.Empty, StringComparison.Ordinal), "`") % 2 == 1)
            {
                return block + "`";
            }

            return block;
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }

    private sealed class TurnObserver(AgentPane pane, TurnView turn) : IAgentObserver
    {
        public void TextDelta(string text) => pane.Post(() => turn.AppendText(text));

        public void ToolStarted(ToolCall call) => pane.Post(() => turn.ToolStarted(call));

        public void ToolFinished(ToolCall call, ToolResult result) => pane.Post(() => turn.ToolFinished(call, result));

        public void StepFinished(ChatUsage? usage) => pane.Post(pane.UpdateFooter);

        public void Notice(string text) => pane.Post(() => turn.Notice(text));
    }

    /// <summary>
    /// Вопрос о запуске скрипта: агент спрашивает из своего потока, окно
    /// показывается в потоке интерфейса через очередь диалогов.
    /// </summary>
    private sealed class ScriptApprover(AgentPane pane) : IScriptApprover
    {
        public Task<bool> ApproveAsync(ScriptRequest request, CancellationToken cancellationToken)
        {
            if (pane.runScriptsWithoutAsking)
            {
                AppLog.Write($"Агент: скрипт «{request.Title}» запущен без вопроса (разрешено в этом диалоге)");
                return Task.FromResult(true);
            }

            if (pane.Dialogs is not { } dialogs)
            {
                return Task.FromResult(false);
            }

            TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = pane.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    (bool run, bool trust) = await ScriptApprovalDialog.ShowAsync(dialogs, request, cancellationToken);
                    if (trust)
                    {
                        pane.runScriptsWithoutAsking = true;
                    }

                    AppLog.Write($"Агент: скрипт «{request.Title}» {(run ? "разрешён" : "отклонён")} пользователем");
                    answer.TrySetResult(run);
                }
                catch (OperationCanceledException)
                {
                    answer.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    AppLog.Write($"Агент: окно подтверждения скрипта: {exception}");
                    answer.TrySetException(exception);
                }
            });
            if (!queued)
            {
                answer.TrySetException(new InvalidOperationException("Окно программы закрыто."));
            }

            return answer.Task;
        }
    }
}
