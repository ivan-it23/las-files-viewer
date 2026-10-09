using AMK.LasViewer.App.Services;
using AMK.LasViewer.Charting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Планшеты рядом: файлы (спуск и подъём, 400 и 2000 кГц, до и после
/// обработки) и совместные планшеты нескольких файлов.
/// </summary>
/// <remarks>
/// При общей глубине прокрутка, масштаб и курсор одного планшета повторяются
/// в остальных — но только у планшетов с той же единицей глубины: метры и
/// футы по одному числу совмещать нельзя.
/// <para>
/// Кривую можно перетащить с шапки одного планшета на трек другого: она
/// появится там как кривая другого файла (см. <see cref="LogTablet"/>).
/// </para>
/// </remarks>
public sealed partial class TabletPanels : UserControl
{
    private static readonly SolidColorBrush FocusedHeader = new(Color.FromArgb(0xFF, 0xDC, 0xE9, 0xF8));
    private static readonly SolidColorBrush NormalHeader = new(Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
    private static readonly SolidColorBrush SeparatorBrush = new(Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8));

    private readonly Grid grid = new();
    private readonly List<PanelView> panels = [];
    private PanelView? focused;
    private bool syncing;
    private bool syncDepth = true;

    public TabletPanels()
    {
        Content = grid;
    }

    /// <summary>Сменился планшет в фокусе или его вид — окну пора обновить панель инструментов и вкладки.</summary>
    public event EventHandler? FocusChanged;

    public event EventHandler? FocusedViewChanged;

    /// <summary>Изменён шаблон или пометки одного из планшетов.</summary>
    public event EventHandler? Edited;

    /// <summary>Добавлена или убрана панель.</summary>
    public event EventHandler? LayoutChanged;

    internal DialogService? Dialogs { get; set; }

    /// <summary>Окно: файлы проекта, из которых планшеты берут кривые.</summary>
    internal ITabletHost? Host { get; set; }

    /// <summary>Всё, что показано, слева направо.</summary>
    internal IReadOnlyList<ITabletContent> Contents => panels.Select(panel => panel.Content).ToList();

    /// <summary>Файлы на панелях (без совместных планшетов).</summary>
    internal IReadOnlyList<OpenedDocument> Documents => panels.Select(panel => panel.Content).OfType<OpenedDocument>().ToList();

    internal ITabletContent? FocusedContent => focused?.Content;

    /// <summary>Файл в фокусе; <c>null</c>, если в фокусе совместный планшет.</summary>
    internal OpenedDocument? FocusedDocument => focused?.Content as OpenedDocument;

    public LogTablet? FocusedTablet => focused?.Tablet;

    public int Count => panels.Count;

    public bool SyncDepth
    {
        get => syncDepth;
        set
        {
            syncDepth = value;
            if (value && focused is not null)
            {
                Propagate(focused);
            }
        }
    }

    /// <summary>Показывает планшет: если он уже на панели — фокус на неё, иначе он заменяет планшет в фокусе.</summary>
    internal void ShowSingle(ITabletContent content)
    {
        if (Find(content) is { } existing)
        {
            Focus(existing);
            return;
        }

        if (focused is null)
        {
            Add(content);
            return;
        }

        SaveView(focused);
        focused.Content = content;
        ShowContent(focused, aligned: true);
        FocusChanged?.Invoke(this, EventArgs.Empty);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Добавляет планшет рядом справа; при общей глубине новый планшет встаёт на ту же глубину.</summary>
    internal void Add(ITabletContent content)
    {
        if (Find(content) is { } existing)
        {
            Focus(existing);
            return;
        }

        PanelView panel = CreatePanel(content);
        panels.Add(panel);
        RebuildGrid();
        ShowContent(panel, aligned: true);
        Focus(panel);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Раскладка из файла проекта.</summary>
    internal void SetLayout(IReadOnlyList<ITabletContent> contents)
    {
        Clear();
        foreach (ITabletContent content in contents.Distinct())
        {
            panels.Add(CreatePanel(content));
        }

        RebuildGrid();
        foreach (PanelView panel in panels)
        {
            ShowContent(panel, aligned: false);
        }

        if (panels.Count > 0)
        {
            Focus(panels[0]);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Remove(ITabletContent content)
    {
        if (Find(content) is not { } panel)
        {
            return;
        }

        SaveView(panel);
        panels.Remove(panel);
        panel.Tablet.Clear();
        RebuildGrid();
        if (focused == panel)
        {
            focused = null;
            if (panels.Count > 0)
            {
                Focus(panels[^1]);
            }
            else
            {
                FocusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool Contains(ITabletContent content) => Find(content) is not null;

    public void Clear()
    {
        foreach (PanelView panel in panels)
        {
            SaveView(panel);
            panel.Tablet.Clear();
        }

        panels.Clear();
        focused = null;
        RebuildGrid();
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Вид каждого планшета — в его содержимое, перед сохранением проекта.</summary>
    public void SaveViews()
    {
        foreach (PanelView panel in panels)
        {
            SaveView(panel);
        }
    }

    /// <summary>Шаблон, пометки или файлы планшета заменены извне (применён шаблон, сдвиг) — перерисовать.</summary>
    internal void RefreshContent(ITabletContent content)
    {
        if (Find(content) is { } panel)
        {
            SaveView(panel);
            ShowContent(panel, aligned: false);
        }
    }

    /// <summary>Перерисовать все планшеты: закрыт файл, на кривые которого они могли ссылаться.</summary>
    internal void RefreshAll()
    {
        foreach (PanelView panel in panels)
        {
            SaveView(panel);
            ShowContent(panel, aligned: false);
        }
    }

    // ------------------------------------------------------------------

    private sealed class PanelView(ITabletContent content, LogTablet tablet, Border header, TextBlock title, Button close, Grid root)
    {
        public ITabletContent Content { get; set; } = content;

        public LogTablet Tablet { get; } = tablet;

        public Border Header { get; } = header;

        public TextBlock Title { get; } = title;

        public Button Close { get; } = close;

        public Grid Root { get; } = root;
    }

    private PanelView? Find(ITabletContent content) => panels.FirstOrDefault(panel => panel.Content == content);

    private PanelView CreatePanel(ITabletContent content)
    {
        LogTablet tablet = new() { Dialogs = Dialogs, Host = Host };
        TextBlock title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Button close = new()
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(close, "Убрать с экрана (файл и совместный планшет остаются в проекте)");

        Grid headerContent = new() { ColumnSpacing = 6 };
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.Children.Add(title);
        Grid.SetColumn(close, 1);
        headerContent.Children.Add(close);

        Border header = new() { Child = headerContent, Padding = new Thickness(10, 3, 4, 3), Background = NormalHeader };
        Grid root = new() { BorderBrush = SeparatorBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(tablet, 1);
        root.Children.Add(tablet);

        PanelView panel = new(content, tablet, header, title, close, root);
        close.Click += (_, _) => Remove(panel.Content);
        root.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Focus(panel)), handledEventsToo: true);
        tablet.ViewChanged += (_, _) => OnViewChanged(panel);
        tablet.CursorDepthChanged += (_, depth) => OnCursorChanged(panel, depth);
        tablet.Edited += (_, _) => Edited?.Invoke(this, EventArgs.Empty);
        return panel;
    }

    private void RebuildGrid()
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        for (int index = 0; index < panels.Count; index++)
        {
            PanelView panel = panels[index];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Root.BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0);
            panel.Close.Visibility = panels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            // Заголовок нужен, когда планшетов несколько, и у совместного
            // планшета — иначе его не отличить от планшета файла.
            panel.Header.Visibility = panels.Count > 1 || panel.Content is CombinedTablet ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(panel.Root, index);
            grid.Children.Add(panel.Root);
        }
    }

    private TabletSources Sources(ITabletContent content) =>
        Host?.SourcesFor(content)
        ?? (content is OpenedDocument document
            ? new TabletSources(document.AsSource(), [])
            : throw new InvalidOperationException("Совместный планшет показывается только в окне проекта."));

    /// <param name="aligned">
    /// При общей глубине встать на глубину соседнего планшета той же единицы,
    /// иначе — на сохранённый вид.
    /// </param>
    private void ShowContent(PanelView panel, bool aligned)
    {
        ITabletContent content = panel.Content;
        TabletSources sources = Sources(content);
        panel.Title.Text = content.Title;
        ToolTipService.SetToolTip(panel.Title, content.Tooltip);
        RebuildGrid();

        (double? top, double? scale) = (content.ViewTop, content.ViewPixelsPerUnit);
        if (aligned && syncDepth)
        {
            PanelView? reference = (focused is not null && focused != panel ? focused : null)
                ?? panels.FirstOrDefault(other => other != panel && other.Tablet.HasData);
            if (reference is not null && reference.Tablet.HasData && SameUnit(reference.Tablet.IndexUnit, sources.IndexUnit))
            {
                (top, scale) = (reference.Tablet.Top, reference.Tablet.PixelsPerUnit);
            }
        }

        panel.Tablet.Show(content, sources, top, scale);
    }

    private void Focus(PanelView panel)
    {
        if (focused == panel)
        {
            return;
        }

        focused = panel;
        foreach (PanelView other in panels)
        {
            other.Header.Background = other == panel ? FocusedHeader : NormalHeader;
        }

        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewChanged(PanelView source)
    {
        SaveView(source);
        if (source == focused)
        {
            FocusedViewChanged?.Invoke(this, EventArgs.Empty);
        }

        if (syncDepth && !syncing)
        {
            Propagate(source);
        }
    }

    private void Propagate(PanelView source)
    {
        if (!source.Tablet.HasData)
        {
            return;
        }

        syncing = true;
        try
        {
            foreach (PanelView other in panels)
            {
                if (other != source && SameUnit(source.Tablet.IndexUnit, other.Tablet.IndexUnit))
                {
                    other.Tablet.SetView(source.Tablet.Top, source.Tablet.PixelsPerUnit);
                    SaveView(other);
                }
            }
        }
        finally
        {
            syncing = false;
        }
    }

    private void OnCursorChanged(PanelView source, double? depth)
    {
        if (!syncDepth)
        {
            return;
        }

        foreach (PanelView other in panels)
        {
            if (other != source && SameUnit(source.Tablet.IndexUnit, other.Tablet.IndexUnit))
            {
                other.Tablet.ShowExternalCursor(depth);
            }
        }
    }

    private static void SaveView(PanelView panel)
    {
        if (panel.Tablet.HasData)
        {
            panel.Content.ViewTop = panel.Tablet.Top;
            panel.Content.ViewPixelsPerUnit = panel.Tablet.PixelsPerUnit;
        }
    }

    private static bool SameUnit(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
}
