using System.Runtime.InteropServices;
using AMK.LasViewer.Core;

namespace AMK.LasViewer.Engine;

/// <summary>Методы движка <c>las.*</c> и перевод их ответов в модель <see cref="LasDocument"/>.</summary>
public static class LasEngineApi
{
    public const int FileNotFound = -32001;
    public const int FileAccessDenied = -32002;
    public const int NotLas = -32003;
    public const int UnknownFileId = -32004;

    private const string SupportedDataEncoding = "f64le-base64";

    // Чтение и передача большого файла занимают заметно дольше обычного запроса.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromMinutes(2);

    public static async Task<LasDocument> OpenLasAsync(this EngineHost engine, string path, CancellationToken cancellationToken)
    {
        OpenResult result = await engine.InvokeAsync<OpenResult>(
            "las.open",
            new { Path = path },
            cancellationToken,
            OpenTimeout);
        return ToDocument(result);
    }

    public static Task CloseLasAsync(this EngineHost engine, string fileId, CancellationToken cancellationToken)
    {
        return engine.InvokeAsync<System.Text.Json.JsonElement>("las.close", new { FileId = fileId }, cancellationToken);
    }

    private static LasDocument ToDocument(OpenResult result)
    {
        if (result.Data.Encoding != SupportedDataEncoding)
        {
            throw new EngineException($"Движок передал данные в неизвестном формате {result.Data.Encoding}.");
        }

        if (result.Data.Columns.Count != result.Curves.Count)
        {
            throw new EngineException(
                $"Движок передал {result.Data.Columns.Count} столбцов данных для {result.Curves.Count} кривых.");
        }

        List<LasCurve> curves = new(result.Curves.Count);
        for (int index = 0; index < result.Curves.Count; index++)
        {
            CurveDto curve = result.Curves[index];
            double[] values = DecodeColumn(result.Data.Columns[index]);
            if (values.Length != result.RowCount)
            {
                throw new EngineException(
                    $"Кривая {curve.Mnemonic}: {values.Length} значений вместо {result.RowCount}.");
            }

            curves.Add(new LasCurve
            {
                Mnemonic = curve.Mnemonic,
                OriginalMnemonic = curve.OriginalMnemonic,
                Unit = curve.Unit,
                ApiCode = curve.ApiCode,
                Description = curve.Description,
                Statistics = new CurveStatistics(
                    curve.Stats.Count,
                    curve.Stats.Nulls,
                    curve.Stats.Min,
                    curve.Stats.Max,
                    curve.Stats.Mean),
                Values = values
            });
        }

        return new LasDocument
        {
            FileId = result.FileId,
            Path = result.Path,
            Name = result.Name,
            SizeBytes = result.SizeBytes,
            EncodingDisplay = result.EncodingDisplay,
            EncodingMethod = result.EncodingMethod,
            Version = result.Version,
            Wrap = result.Wrap,
            Delimiter = result.Delimiter,
            NullValue = result.NullValue,
            RowCount = result.RowCount,
            Index = new LasIndexInfo(
                result.Index.Mnemonic,
                result.Index.Unit,
                result.Index.Start,
                result.Index.Stop,
                result.Index.Step,
                ParseDirection(result.Index.Direction),
                result.Index.HeaderStart,
                result.Index.HeaderStop,
                result.Index.HeaderStep),
            Sections = result.Sections
                .Select(section => new LasSection(
                    ParseSectionKind(section.Kind),
                    section.Title,
                    section.Line,
                    section.Items
                        .Select(item => new LasHeaderItem(item.Mnemonic, item.Unit, item.Value, item.Description, item.Line))
                        .ToList(),
                    section.Text))
                .ToList(),
            Curves = curves,
            Issues = result.Issues
                .Select(issue => new LasIssue(ParseLevel(issue.Level), issue.Code, issue.Message, issue.Line))
                .ToList()
        };
    }

    private static double[] DecodeColumn(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(double) != 0)
        {
            throw new EngineException("Длина столбца данных не кратна 8 байтам.");
        }

        double[] values = MemoryMarshal.Cast<byte, double>(bytes).ToArray();
        if (!BitConverter.IsLittleEndian)
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = BitConverter.Int64BitsToDouble(
                    System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToInt64Bits(values[index])));
            }
        }

        return values;
    }

    private static LasSectionKind ParseSectionKind(string kind) => kind switch
    {
        "version" => LasSectionKind.Version,
        "well" => LasSectionKind.Well,
        "curve" => LasSectionKind.Curve,
        "parameter" => LasSectionKind.Parameter,
        "other" => LasSectionKind.Other,
        "data" => LasSectionKind.Data,
        _ => LasSectionKind.Unknown
    };

    private static LasIssueLevel ParseLevel(string level) => level switch
    {
        "error" => LasIssueLevel.Error,
        "warning" => LasIssueLevel.Warning,
        _ => LasIssueLevel.Info
    };

    private static IndexDirection ParseDirection(string direction) => direction switch
    {
        "increasing" => IndexDirection.Increasing,
        "decreasing" => IndexDirection.Decreasing,
        "unordered" => IndexDirection.Unordered,
        _ => IndexDirection.Unknown
    };

    private sealed record OpenResult(
        string FileId,
        string Path,
        string Name,
        long SizeBytes,
        string EncodingDisplay,
        string EncodingMethod,
        string Version,
        bool Wrap,
        string Delimiter,
        double? NullValue,
        int RowCount,
        IndexDto Index,
        List<SectionDto> Sections,
        List<CurveDto> Curves,
        List<IssueDto> Issues,
        DataDto Data);

    private sealed record IndexDto(
        string Mnemonic,
        string Unit,
        double? Start,
        double? Stop,
        double? Step,
        string Direction,
        double? HeaderStart,
        double? HeaderStop,
        double? HeaderStep);

    private sealed record SectionDto(string Kind, string Title, int Line, List<HeaderItemDto> Items, string Text);

    private sealed record HeaderItemDto(string Mnemonic, string Unit, string Value, string Description, int Line);

    private sealed record CurveDto(
        string Mnemonic,
        string OriginalMnemonic,
        string Unit,
        string ApiCode,
        string Description,
        StatsDto Stats);

    private sealed record StatsDto(int Count, int Nulls, double? Min, double? Max, double? Mean);

    private sealed record IssueDto(string Level, string Code, string Message, int? Line);

    private sealed record DataDto(string Encoding, List<string> Columns);
}
