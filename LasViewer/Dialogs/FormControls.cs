using System.Globalization;
using AMK.LasViewer.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AMK.LasViewer.App.Dialogs;

/// <summary>Выбор цвета из палитры планшета или по коду #RRGGBB.</summary>
internal sealed partial class ColorSwatchPicker : StackPanel
{
    private static readonly uint[] Palette =
    [
        0xFF1F5FBF, 0xFFD62728, 0xFF2CA02C, 0xFF8E44AD, 0xFFE67E22, 0xFF7F5539,
        0xFF000000, 0xFF6E6E6E, 0xFF0B3C8C, 0xFF17A2B8, 0xFF808000, 0xFFE377C2,
        0xFF8B0000, 0xFFE6A100, 0xFF00BCD4, 0xFF2E7D32
    ];

    private readonly List<(Button Button, uint Color)> swatches = [];
    private readonly TextBox hexBox = new() { Width = 110, PlaceholderText = "#RRGGBB" };
    private uint value;

    public ColorSwatchPicker(uint initial)
    {
        Spacing = 6;
        VariableSizedWrapGrid grid = new() { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 8, ItemWidth = 30, ItemHeight = 30 };
        foreach (uint color in Palette)
        {
            Button button = new()
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = SceneRenderer.Brush(color),
                BorderThickness = new Thickness(2)
            };
            ToolTipService.SetToolTip(button, $"#{color & 0xFFFFFF:X6}");
            button.Click += (_, _) => Value = color;
            swatches.Add((button, color));
            grid.Children.Add(button);
        }

        hexBox.LostFocus += (_, _) => ApplyHex();
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyHex();
            }
        };

        Children.Add(grid);
        Children.Add(hexBox);
        Value = initial;
    }

    /// <summary>Цвет без прозрачности: прозрачность задаёт место использования (заливка, линия).</summary>
    public uint Value
    {
        get => value;
        set
        {
            this.value = value | 0xFF000000;
            hexBox.Text = $"#{this.value & 0xFFFFFF:X6}";
            foreach ((Button button, uint color) in swatches)
            {
                button.BorderBrush = color == this.value
                    ? new SolidColorBrush(Colors.Black)
                    : new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            }
        }
    }

    private void ApplyHex()
    {
        string text = hexBox.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
        {
            Value = parsed;
        }
        else
        {
            Value = value;
        }
    }
}

/// <summary>Двухколоночная форма «подпись — поле» для диалогов.</summary>
internal sealed class Form
{
    private readonly Grid grid = new() { ColumnSpacing = 12, RowSpacing = 10, MinWidth = 380 };

    public Form()
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0x1C, 0x28)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
    }

    public TextBlock Error { get; }

    public FrameworkElement Root
    {
        get
        {
            StackPanel panel = new() { Spacing = 12 };
            panel.Children.Add(grid);
            panel.Children.Add(Error);
            return new ScrollViewer { Content = panel, MaxHeight = 560 };
        }
    }

    public T Add<T>(string label, T field) where T : FrameworkElement
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        field.Tag = caption;
        return field;
    }

    public void SetVisible(FrameworkElement field, bool visible)
    {
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        field.Visibility = visibility;
        if (field.Tag is TextBlock caption)
        {
            caption.Visibility = visibility;
        }
    }

    public void ShowError(string? message)
    {
        Error.Text = message ?? string.Empty;
        Error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static NumberBox Number(double value, double minimum = double.MinValue, double maximum = double.MaxValue, double step = 1)
    {
        return new NumberBox
        {
            Value = value,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = 0,
                IsDecimalPointAlwaysDisplayed = false,
                NumberRounder = new Windows.Globalization.NumberFormatting.SignificantDigitsNumberRounder { SignificantDigits = 8 }
            }
        };
    }

    public static ComboBox Choice(IEnumerable<string> items, int selected)
    {
        ComboBox box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string item in items)
        {
            box.Items.Add(item);
        }

        box.SelectedIndex = selected;
        return box;
    }

    public static double Read(NumberBox box) => double.IsNaN(box.Value) ? double.NaN : box.Value;
}
