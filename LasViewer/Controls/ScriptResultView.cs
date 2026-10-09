using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AMK.LasViewer.Agent;
using AMK.LasViewer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AMK.LasViewer.App.Controls;

/// <summary>
/// Итог скрипта агента в чате: графики, таблицы и файлы со ссылками, папка
/// запуска, код и вывод скрипта. Строится по результату движка
/// (<c>agent.run_script</c>), который модель получает тем же JSON.
/// </summary>
internal static class ScriptResultView
{
    private const int MaxImageHeight = 420;

    public static FrameworkElement? Build(ToolCall call, ToolResult result)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(result.Json).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        // Удачный запуск — подробности в "result", неудачный — рядом с "error".
        JsonElement details = root.TryGetProperty("result", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        StackPanel panel = new() { Spacing = 6, Margin = new Thickness(18, 0, 0, 4) };

        foreach ((string name, string path) in Files(details, "figures"))
        {
            panel.Children.Add(Figure(name, path));
        }

        StackPanel links = new() { Spacing = 0 };
        foreach ((string name, string path) in Files(details, "tables").Concat(Files(details, "files")))
        {
            links.Children.Add(Link(name, () => Open(path), "Открыть"));
        }

        if (Text(details, "folder") is { Length: > 0 } folder && Directory.Exists(folder))
        {
            links.Children.Add(Link("Открыть папку скрипта", () => OpenFolder(folder), folder));
        }

        if (links.Children.Count > 0)
        {
            panel.Children.Add(links);
        }

        string code = Code(call);
        if (code.Length > 0)
        {
            panel.Children.Add(Section("Код скрипта", code));
        }

        string output = Text(details, "output").TrimEnd();
        string traceback = Text(details, "traceback").TrimEnd();
        string console = Text(details, "console").TrimEnd();
        string printed = string.Join("\n\n", new[] { output, traceback, console }.Where(part => part.Length > 0));
        if (printed.Length > 0)
        {
            panel.Children.Add(Section(result.Ok ? "Вывод скрипта" : "Вывод и ошибка скрипта", printed));
        }

        return panel.Children.Count > 0 ? panel : null;
    }

    private static FrameworkElement Figure(string name, string path)
    {
        Image image = new() { Stretch = Stretch.Uniform, MaxHeight = MaxImageHeight, HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, name);
        _ = LoadAsync(image, path);

        HyperlinkButton button = new() { Content = image, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTipService.SetToolTip(button, name + " — открыть в программе просмотра");
        button.Click += (_, _) => Open(path);

        StackPanel figure = new() { Spacing = 2 };
        figure.Children.Add(button);
        figure.Children.Add(new TextBlock { Text = name, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        return figure;
    }

    /// <summary>Картинка читается в память: файл не остаётся занятым, его можно удалить или заменить.</summary>
    private static async Task LoadAsync(Image image, string path)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            using MemoryStream memory = new(bytes);
            BitmapImage bitmap = new() { DecodePixelWidth = 1200 };
            await bitmap.SetSourceAsync(memory.AsRandomAccessStream());
            image.Source = bitmap;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            AppLog.Write($"Агент: график {path} не показан: {exception.Message}");
        }
    }

    private static Expander Section(string header, string text)
    {
        TextBlock body = new()
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap
        };
        return new Expander
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 360,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto
            }
        };
    }

    private static HyperlinkButton Link(string text, Action click, string tooltip)
    {
        HyperlinkButton link = new()
        {
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            Padding = new Thickness(0, 2, 0, 2)
        };
        ToolTipService.SetToolTip(link, tooltip);
        link.Click += (_, _) => click();
        return link;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write($"Агент: не открыт {path}: {exception.Message}");
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Агент: папка {folder} не открыта: {exception.Message}");
        }
    }

    private static IEnumerable<(string Name, string Path)> Files(JsonElement details, string property)
    {
        if (!details.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (Text(item, "path") is { Length: > 0 } path && File.Exists(path))
            {
                yield return (Text(item, "name"), path);
            }
        }
    }

    private static string Code(ToolCall call)
    {
        try
        {
            return Text(JsonDocument.Parse(call.Arguments).RootElement, "code").TrimEnd();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
