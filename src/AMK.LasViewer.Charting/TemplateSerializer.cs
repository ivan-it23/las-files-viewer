using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AMK.LasViewer.Charting;

/// <summary>
/// Шаблоны планшета и пометки в JSON. Формат версионирован: файл новее, чем
/// понимает программа, не читается молча с потерей полей, а отвергается.
/// </summary>
/// <remarks>
/// Схема описана здесь явно, а не выводится из классов: переименование
/// свойства в коде не должно ломать шаблоны, которые пользователи уже
/// сохранили.
/// </remarks>
public static class TemplateSerializer
{
    public const string TemplateFormat = "lasviewer-template";
    public const int TemplateVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string WriteTemplate(TabletTemplate template)
    {
        JsonObject root = new()
        {
            ["format"] = TemplateFormat,
            ["version"] = TemplateVersion,
            ["template"] = TemplateToJson(template)
        };
        return root.ToJsonString(WriteOptions);
    }

    public static TabletTemplate ReadTemplate(string json)
    {
        JsonObject root = ParseObject(json);
        RequireFormat(root, TemplateFormat, TemplateVersion, "шаблон");
        return TemplateFromJson(Object(root, "template"));
    }

    public static JsonObject TemplateToJson(TabletTemplate template)
    {
        JsonArray tracks = [];
        foreach (TrackDefinition track in template.Tracks)
        {
            JsonArray curves = [];
            foreach (CurveStyle curve in track.Curves)
            {
                curves.Add(new JsonObject
                {
                    ["mnemonic"] = curve.Mnemonic,
                    ["color"] = ColorToString(curve.Color),
                    ["thickness"] = curve.Thickness,
                    ["dash"] = curve.Dash.ToString().ToLowerInvariant(),
                    ["scale"] = curve.Scale is null ? null : ScaleToJson(curve.Scale),
                    ["wrap"] = curve.Wrap.ToString().ToLowerInvariant()
                });
            }

            JsonArray fills = [];
            foreach (FillDefinition fill in track.Fills)
            {
                fills.Add(new JsonObject
                {
                    ["kind"] = FillKindName(fill.Kind),
                    ["curve"] = fill.Curve,
                    ["other_curve"] = fill.OtherCurve,
                    ["value"] = fill.Value,
                    ["color"] = ColorToString(fill.Color),
                    ["opposite_color"] = fill.OppositeColor is { } opposite ? ColorToString(opposite) : null
                });
            }

            tracks.Add(new JsonObject
            {
                ["width"] = track.WidthDip,
                ["scale"] = ScaleToJson(track.Scale),
                ["curves"] = curves,
                ["fills"] = fills
            });
        }

        return new JsonObject
        {
            ["name"] = template.Name,
            ["depth_track_width"] = template.DepthTrackWidth,
            ["tracks"] = tracks
        };
    }

    public static TabletTemplate TemplateFromJson(JsonObject json)
    {
        TabletTemplate template = new()
        {
            Name = String(json, "name", string.Empty),
            DepthTrackWidth = Number(json, "depth_track_width", 72)
        };

        foreach (JsonObject track in Array(json, "tracks"))
        {
            TrackDefinition definition = new()
            {
                Scale = ScaleFromJson(Object(track, "scale")),
                WidthDip = Number(track, "width", TabletTemplate.DefaultTrackWidth)
            };

            foreach (JsonObject curve in Array(track, "curves"))
            {
                definition.Curves.Add(new CurveStyle
                {
                    Mnemonic = RequiredString(curve, "mnemonic"),
                    Color = ColorFromString(String(curve, "color", "#FF1F5FBF")),
                    Thickness = Number(curve, "thickness", 1.2),
                    Dash = Enum<LineDash>(curve, "dash", LineDash.Solid),
                    Scale = curve["scale"] is JsonObject scale ? ScaleFromJson(scale) : null,
                    Wrap = Enum<WrapMode>(curve, "wrap", WrapMode.Clamp)
                });
            }

            foreach (JsonObject fill in Array(track, "fills"))
            {
                definition.Fills.Add(new FillDefinition
                {
                    Kind = FillKindFromName(RequiredString(fill, "kind")),
                    Curve = RequiredString(fill, "curve"),
                    OtherCurve = fill["other_curve"]?.GetValue<string>(),
                    Value = Number(fill, "value", 0),
                    Color = ColorFromString(String(fill, "color", "#553A7BD5")),
                    OppositeColor = fill["opposite_color"] is JsonValue opposite ? ColorFromString(opposite.GetValue<string>()) : null
                });
            }

            template.Tracks.Add(definition);
        }

        return template;
    }

    public static JsonObject AnnotationsToJson(DocumentAnnotations annotations)
    {
        JsonArray markers = [];
        foreach (DepthMarker marker in annotations.Markers)
        {
            markers.Add(new JsonObject
            {
                ["id"] = marker.Id.ToString(),
                ["depth"] = marker.Depth,
                ["name"] = marker.Name,
                ["note"] = marker.Note,
                ["color"] = ColorToString(marker.Color)
            });
        }

        JsonArray intervals = [];
        foreach (DepthInterval interval in annotations.Intervals)
        {
            intervals.Add(new JsonObject
            {
                ["id"] = interval.Id.ToString(),
                ["top"] = interval.Top,
                ["bottom"] = interval.Bottom,
                ["name"] = interval.Name,
                ["note"] = interval.Note,
                ["color"] = ColorToString(interval.Color)
            });
        }

        return new JsonObject { ["markers"] = markers, ["intervals"] = intervals };
    }

    public static DocumentAnnotations AnnotationsFromJson(JsonObject? json)
    {
        DocumentAnnotations annotations = new();
        if (json is null)
        {
            return annotations;
        }

        foreach (JsonObject marker in Array(json, "markers"))
        {
            annotations.Markers.Add(new DepthMarker
            {
                Id = Guid.TryParse(String(marker, "id", string.Empty), out Guid id) ? id : Guid.NewGuid(),
                Depth = Number(marker, "depth", 0),
                Name = String(marker, "name", string.Empty),
                Note = String(marker, "note", string.Empty),
                Color = ColorFromString(String(marker, "color", "#FFB00020"))
            });
        }

        foreach (JsonObject interval in Array(json, "intervals"))
        {
            annotations.Intervals.Add(new DepthInterval
            {
                Id = Guid.TryParse(String(interval, "id", string.Empty), out Guid id) ? id : Guid.NewGuid(),
                Top = Number(interval, "top", 0),
                Bottom = Number(interval, "bottom", 0),
                Name = String(interval, "name", string.Empty),
                Note = String(interval, "note", string.Empty),
                Color = ColorFromString(String(interval, "color", "#FFE6A100"))
            });
        }

        return annotations;
    }

    public static string ColorToString(uint argb) => $"#{argb:X8}";

    public static uint ColorFromString(string text)
    {
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        return hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new FormatException($"Неверный цвет «{text}»: ожидается #RRGGBB или #AARRGGBB.");
    }

    public static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Файл не содержит объекта JSON.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Файл повреждён: {exception.Message}", exception);
        }
    }

    public static void RequireFormat(JsonObject root, string format, int supportedVersion, string what)
    {
        if (String(root, "format", string.Empty) != format)
        {
            throw new FormatException($"Это не {what} просмотрщика LAS.");
        }

        int version = (int)Number(root, "version", 0);
        if (version < 1 || version > supportedVersion)
        {
            throw new FormatException(
                $"{char.ToUpperInvariant(what[0])}{what[1..]} версии {version} создан более новой программой. Обновите просмотрщик.");
        }
    }

    private static JsonObject ScaleToJson(TrackScale scale) => new()
    {
        ["kind"] = scale.Kind == ScaleKind.Logarithmic ? "log" : "linear",
        ["left"] = scale.Left,
        ["right"] = scale.Right
    };

    private static TrackScale ScaleFromJson(JsonObject json)
    {
        double left = Number(json, "left", 0);
        double right = Number(json, "right", 1);
        return String(json, "kind", "linear") == "log"
            ? TrackScale.Logarithmic(left, right)
            : TrackScale.Linear(left, right);
    }

    private static string FillKindName(FillKind kind) => kind switch
    {
        FillKind.ToLeftEdge => "left_edge",
        FillKind.ToRightEdge => "right_edge",
        FillKind.ToValue => "value",
        _ => "between"
    };

    private static FillKind FillKindFromName(string name) => name switch
    {
        "left_edge" => FillKind.ToLeftEdge,
        "right_edge" => FillKind.ToRightEdge,
        "value" => FillKind.ToValue,
        "between" => FillKind.BetweenCurves,
        _ => throw new FormatException($"Неизвестный вид заливки «{name}».")
    };

    public static JsonObject Object(JsonObject json, string name) =>
        json[name] as JsonObject ?? throw new FormatException($"Нет обязательного раздела «{name}».");

    public static IEnumerable<JsonObject> Array(JsonObject json, string name) =>
        (json[name] as JsonArray)?.OfType<JsonObject>() ?? [];

    public static string String(JsonObject json, string name, string fallback) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) ? text : fallback;

    public static string RequiredString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0
            ? text
            : throw new FormatException($"Нет обязательного поля «{name}».");

    public static double Number(JsonObject json, string name, double fallback) =>
        json[name] is JsonValue value && value.TryGetValue(out double number) ? number : fallback;

    private static T Enum<T>(JsonObject json, string name, T fallback) where T : struct, System.Enum =>
        System.Enum.TryParse(String(json, name, string.Empty), ignoreCase: true, out T parsed) ? parsed : fallback;
}
