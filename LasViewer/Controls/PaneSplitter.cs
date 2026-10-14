using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Граница боковой панели, которую можно тянуть мышью: меняет ширину
/// столбца <see cref="Column"/>. Лежит поверх края панели, курсор — «↔».
/// </summary>
/// <remarks>
/// В WinUI 3 нет своего GridSplitter, а пакет ради одной полосы не нужен.
/// Наибольшую ширину задаёт окно (<see cref="MaximumWidth"/>): планшету
/// должно остаться место.
/// </remarks>
internal sealed partial class PaneSplitter : Grid
{
    private static readonly SolidColorBrush Idle = new(Colors.Transparent);
    private static readonly SolidColorBrush Active = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD7));

    private readonly Border line = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Background = Idle };
    private double startX;
    private double startWidth;
    private bool dragging;

    public PaneSplitter()
    {
        Width = 6;
        Background = Idle; // прозрачный фон, чтобы полоса ловила мышь
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Children.Add(line);
        PointerEntered += (_, _) => line.Background = Active;
        PointerExited += (_, _) => line.Background = dragging ? Active : Idle;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => { dragging = false; line.Background = Idle; };
        DoubleTapped += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Столбец, ширину которого меняет полоса.</summary>
    public ColumnDefinition? Column { get; set; }

    /// <summary>Полоса у левого края панели (панель агента): тянуть влево — шире.</summary>
    public bool GrowsToLeft { get; set; }

    /// <summary>Наибольшая ширина панели сейчас; без неё — без ограничения.</summary>
    public Func<double>? MaximumWidth { get; set; }

    /// <summary>Ширина изменена перетаскиванием.</summary>
    public event EventHandler? Resized;

    /// <summary>Двойной щелчок — вернуть ширину по умолчанию.</summary>
    public event EventHandler? ResetRequested;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Column is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        startX = e.GetCurrentPoint(null).Position.X;
        startWidth = Column.ActualWidth;
        dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging || Column is null)
        {
            return;
        }

        double shift = e.GetCurrentPoint(null).Position.X - startX;
        double width = startWidth + (GrowsToLeft ? -shift : shift);
        double maximum = MaximumWidth?.Invoke() ?? double.PositiveInfinity;
        width = Math.Max(Column.MinWidth, Math.Min(width, Math.Max(Column.MinWidth, maximum)));
        Column.Width = new GridLength(width);
        Resized?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!dragging)
        {
            return;
        }

        dragging = false;
        ReleasePointerCapture(e.Pointer);
        line.Background = Idle;
        e.Handled = true;
    }
}
