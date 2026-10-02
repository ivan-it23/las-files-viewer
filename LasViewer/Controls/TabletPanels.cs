using AMK.LasViewer.App.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Планшеты нескольких файлов рядом: спуск и подъём, 400 и 2000 кГц, до и
/// после обработки.
/// </summary>
/// <remarks>
/// При общей глубине прокрутка, масштаб и курсор одного планшета повторяются
/// в остальных — но только у файлов с той же единицей глубины: метры и футы
/// по одному числу совмещать нельзя.
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

    /// <summary>Сменился файл в фокусе или его вид — окну пора обновить панель инструментов и вкладки.</summary>
    public event EventHandler? FocusChanged;

    public event EventHandler? FocusedViewChanged;

    /// <summary>Изменён шаблон или пометки одного из файлов.</summary>
    public event EventHandler? Edited;

    /// <summary>Добавлена или убрана панель.</summary>
    public event EventHandler? LayoutChanged;

    internal DialogService? Dialogs { get; set; }

    internal IReadOnlyList<OpenedDocument> Documents => panels.Select(panel => panel.Document).ToList();

    internal OpenedDocument? FocusedDocument => focused?.Document;

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

    /// <summary>Показывает файл: если он уже на панели — фокус на неё, иначе он заменяет файл в фокусе.</summary>
    internal void ShowSingle(OpenedDocument document)
    {
        if (Find(document) is { } existing)
        {
            Focus(existing);
            return;
        }

        if (focused is null)
        {
            Add(document);
            return;
        }

        SaveView(focused);
        focused.Document = document;
        ShowDocument(focused, AlignedView(focused, document));
        FocusChanged?.Invoke(this, EventArgs.Empty);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Добавляет файл рядом справа; при общей глубине новый планшет встаёт на ту же глубину.</summary>
    internal void Add(OpenedDocument document)
    {
        if (Find(document) is { } existing)
        {
            Focus(existing);
            return;
        }

        PanelView panel = CreatePanel(document);
        panels.Add(panel);
        RebuildGrid();
        ShowDocument(panel, AlignedView(panel, document));
        Focus(panel);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Раскладка из файла проекта.</summary>
    internal void SetLayout(IReadOnlyList<OpenedDocument> documents)
    {
        Clear();
        foreach (OpenedDocument document in documents.Distinct())
        {
            PanelView panel = CreatePanel(document);
            panels.Add(panel);
        }

        RebuildGrid();
        foreach (PanelView panel in panels)
        {
            ShowDocument(panel, (panel.Document.ViewTop, panel.Document.ViewPixelsPerUnit));
        }

        if (panels.Count > 0)
        {
            Focus(panels[0]);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Remove(OpenedDocument document)
    {
        if (Find(document) is not { } panel)
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

    internal bool Contains(OpenedDocument document) => Find(document) is not null;

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

    /// <summary>Вид каждого планшета — в его документ, перед сохранением проекта.</summary>
    public void SaveViews()
    {
        foreach (PanelView panel in panels)
        {
            SaveView(panel);
        }
    }

    /// <summary>Шаблон или пометки файла заменены извне (применён шаблон) — перерисовать.</summary>
    internal void RefreshDocument(OpenedDocument document)
    {
        if (Find(document) is { } panel)
        {
            SaveView(panel);
            ShowDocument(panel, (document.ViewTop, document.ViewPixelsPerUnit));
        }
    }

    // ------------------------------------------------------------------

    private sealed class PanelView(OpenedDocument document, LogTablet tablet, Border header, TextBlock title, Button close, Grid root)
    {
        public OpenedDocument Document { get; set; } = document;

        public LogTablet Tablet { get; } = tablet;

        public Border Header { get; } = header;

        public TextBlock Title { get; } = title;

        public Button Close { get; } = close;

        public Grid Root { get; } = root;
    }

    private PanelView? Find(OpenedDocument document) => panels.FirstOrDefault(panel => panel.Document == document);

    private PanelView CreatePanel(OpenedDocument document)
    {
        LogTablet tablet = new() { Dialogs = Dialogs };
        TextBlock title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Button close = new()
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(close, "Убрать с планшета (файл остаётся открытым)");

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

        PanelView panel = new(document, tablet, header, title, close, root);
        close.Click += (_, _) => Remove(panel.Document);
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
            panel.Header.Visibility = panels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(panel.Root, index);
            grid.Children.Add(panel.Root);
        }
    }

    private void ShowDocument(PanelView panel, (double? Top, double? PixelsPerUnit) view)
    {
        OpenedDocument document = panel.Document;
        panel.Title.Text = document.Document.Name;
        ToolTipService.SetToolTip(panel.Title, document.Document.Path);
        panel.Tablet.Show(
            document.TabletData,
            document.Template,
            document.Annotations,
            document.Document.FileId,
            view.Top,
            view.PixelsPerUnit);
    }

    /// <summary>
    /// Вид для файла на панели: при общей глубине — как у соседнего планшета
    /// той же единицы глубины, иначе — сохранённый вид файла.
    /// </summary>
    private (double? Top, double? PixelsPerUnit) AlignedView(PanelView target, OpenedDocument document)
    {
        if (syncDepth)
        {
            PanelView? reference = (focused is not null && focused != target ? focused : null)
                ?? panels.FirstOrDefault(panel => panel != target && panel.Tablet.HasData);
            if (reference is not null && reference.Tablet.HasData && SameUnit(reference.Document, document))
            {
                return (reference.Tablet.Top, reference.Tablet.PixelsPerUnit);
            }
        }

        return (document.ViewTop, document.ViewPixelsPerUnit);
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
                if (other != source && SameUnit(source.Document, other.Document))
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
            if (other != source && SameUnit(source.Document, other.Document))
            {
                other.Tablet.ShowExternalCursor(depth);
            }
        }
    }

    private static void SaveView(PanelView panel)
    {
        if (panel.Tablet.HasData)
        {
            panel.Document.ViewTop = panel.Tablet.Top;
            panel.Document.ViewPixelsPerUnit = panel.Tablet.PixelsPerUnit;
        }
    }

    private static bool SameUnit(OpenedDocument first, OpenedDocument second) =>
        string.Equals(first.TabletData.IndexUnit.Trim(), second.TabletData.IndexUnit.Trim(), StringComparison.OrdinalIgnoreCase);
}
