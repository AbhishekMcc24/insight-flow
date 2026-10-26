using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors;

/// <summary>Why DuckDB could not read a workbook or a sheet.</summary>
public enum SpreadsheetFailure
{
    /// <summary>DuckDB reported a parse problem (its message is in <see cref="SpreadsheetException.Technical"/>).</summary>
    Parse,

    /// <summary>DuckDB's Excel reader could not be installed or loaded (for example no internet the first time).</summary>
    EngineUnavailable,
}

/// <summary>A spreadsheet problem with DuckDB's own first-line message. It names cells and types, never cell contents beyond the one value it quotes.</summary>
public sealed class SpreadsheetException : Exception
{
    public SpreadsheetException()
    {
        Technical = string.Empty;
    }

    public SpreadsheetException(string technical)
        : base(technical)
    {
        Technical = technical;
    }

    public SpreadsheetException(string technical, SpreadsheetFailure failure)
        : base(technical)
    {
        Technical = technical;
        Failure = failure;
    }

    public SpreadsheetException(string technical, Exception innerException)
        : base(technical, innerException)
    {
        Technical = technical;
    }

    public string Technical { get; }

    public SpreadsheetFailure Failure { get; }
}

/// <summary>What the person has chosen for a sheet so far (the defaults are DuckDB's own behaviour).</summary>
public sealed record SheetSettings(bool Header = true, string? Range = null, bool AllVarchar = false, bool IgnoreErrors = false)
{
    /// <summary>A short note saying how the sheet was read, when that is not the default.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (AllVarchar)
        {
            parts.Add("every column read as text");
        }

        if (IgnoreErrors)
        {
            parts.Add("cells that did not fit left empty");
        }

        if (!Header)
        {
            parts.Add("first row read as data");
        }

        if (!string.IsNullOrWhiteSpace(Range))
        {
            parts.Add($"range {Range}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

/// <summary>How one sheet turned out: parsed into a table, empty, or a problem for the person to resolve.</summary>
public abstract record SheetOutcome;

public sealed record SheetParsed(DatasetVersion Version, long Rows, int Columns, SheetLayout? Layout = null, long TotalRows = 0) : SheetOutcome;

public sealed record SheetEmpty : SheetOutcome;

/// <summary>DuckDB could not parse the sheet. <see cref="Message"/> is for the person; <see cref="Technical"/> is DuckDB's own line.</summary>
public sealed record SheetFailed(string Kind, string Message, string? Technical, IReadOnlyList<ProblemOption> Options) : SheetOutcome;

/// <summary>
/// Turns what DuckDB says about a sheet it could not parse into a problem a person can act on: where it went wrong (sheet, cell, value),
/// in plain words, and the ways forward. Pure text work, so it is unit-tested with real DuckDB messages.
/// </summary>
public static partial class SpreadsheetProblems
{
    public const string TypeConflict = "type_conflict";
    public const string NoData = "no_data";
    public const string ParseError = "parse_error";
    public const string Unreadable = "unreadable_file";
    public const string EngineUnavailable = "engine_unavailable";
    public const string Unsupported = "unsupported_format";

    private static readonly ProblemOption AsText = new(ProblemChoices.AsText, "Read every column of this sheet as text (nothing is lost)");
    private static readonly ProblemOption IgnoreErrors = new(ProblemChoices.IgnoreErrors, "Keep the typed columns and leave the cells that do not fit empty");
    private static readonly ProblemOption Range = new(ProblemChoices.Range, "Read only a cell range", NeedsText: true, TextHint: "for example A3:H200");
    private static readonly ProblemOption NoHeader = new(ProblemChoices.NoHeader, "Treat the first row as data, not as column names");
    private static readonly ProblemOption SkipSheet = new(ProblemChoices.SkipSheet, "Skip this sheet");
    private static readonly ProblemOption SkipFile = new(ProblemChoices.SkipFile, "Skip this file");
    private static readonly ProblemOption Retry = new(ProblemChoices.Retry, "Try again");

    [GeneratedRegex(@"Failed to parse cell '(?<cell>[A-Z]+\d+)'.*?Could not convert string '(?<value>.*)' to (?<type>[A-Za-z0-9_()]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CellConversion();

    /// <summary>Classifies DuckDB's message for a sheet that failed to parse with <paramref name="tried"/>.</summary>
    public static SheetOutcome Classify(SpreadsheetException error, string sheet, SheetSettings tried)
    {
        ArgumentNullException.ThrowIfNull(error);
        var technical = error.Technical;

        if (error.Failure == SpreadsheetFailure.EngineUnavailable)
        {
            return new SheetFailed(
                EngineUnavailable,
                "DuckDB's Excel reader is not available yet. It is downloaded once the first time, so the server needs internet access for that. " + technical,
                technical,
                [Retry, SkipFile]);
        }

        if (technical.Contains("No rows found", StringComparison.OrdinalIgnoreCase))
        {
            return new SheetEmpty();
        }

        if (technical.Contains("Failed to open zip", StringComparison.OrdinalIgnoreCase) || technical.Contains("not a valid", StringComparison.OrdinalIgnoreCase))
        {
            return new SheetFailed(Unreadable, "This file could not be opened as an .xlsx workbook. It may be damaged, password-protected or not really an Excel file.", technical, [SkipFile]);
        }

        if (CellConversion().Match(technical) is { Success: true } match)
        {
            var value = match.Groups["value"].Value;
            var shown = value.Length > 40 ? value[..40] + "…" : value;
            var type = FriendlyType(match.Groups["type"].Value);
            return new SheetFailed(
                TypeConflict,
                $"On sheet “{sheet}”, cell {match.Groups["cell"].Value} holds “{shown}”, but DuckDB expected {type} there, like the cells above it.",
                technical,
                [AsText, IgnoreErrors, SkipSheet]);
        }

        if (technical.Contains("not found in xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return new SheetFailed(ParseError, $"The sheet “{sheet}” could not be found in the workbook.", technical, [SkipSheet]);
        }

        var options = new List<ProblemOption>();
        if (!tried.AllVarchar)
        {
            options.Add(AsText);
        }

        if (string.IsNullOrWhiteSpace(tried.Range))
        {
            options.Add(Range);
        }

        if (tried.Header)
        {
            options.Add(NoHeader);
        }

        options.Add(SkipSheet);
        return new SheetFailed(ParseError, $"DuckDB could not parse sheet “{sheet}”: {technical}", technical, options);
    }

    /// <summary>A sheet that parsed but has no data rows, although it is not blank (often a title above the table).</summary>
    public static SheetFailed NoDataRows(string sheet, SheetSettings tried)
    {
        var options = new List<ProblemOption>();
        if (string.IsNullOrWhiteSpace(tried.Range))
        {
            options.Add(Range);
        }

        if (tried.Header)
        {
            options.Add(NoHeader);
        }

        options.Add(SkipSheet);
        return new SheetFailed(
            NoData,
            $"DuckDB found no data rows on sheet “{sheet}”. Tables often start lower down, below a title or notes; name the cells the table covers.",
            null,
            options);
    }

    /// <summary>Old binary workbooks are not read by DuckDB's reader.</summary>
    public static SheetFailed UnsupportedFormat(string extension) =>
        new(Unsupported, $"DuckDB reads .xlsx workbooks only; “{extension}” files are not supported yet. Open it in Excel, save it as .xlsx and prepare it again.", null, [SkipFile]);

    private static string FriendlyType(string duckType) => duckType.ToUpperInvariant() switch
    {
        "DOUBLE" or "BIGINT" or "INTEGER" or "DECIMAL" or "FLOAT" or "HUGEINT" => "a number",
        "DATE" => "a date",
        "TIMESTAMP" => "a date and time",
        "BOOLEAN" => "yes/no",
        _ => duckType.ToLowerInvariant(),
    };
}

/// <summary>
/// Reads Excel workbooks with DuckDB: lists the sheets of a workbook, then parses each sheet to a typed table saved as Parquet. A sheet DuckDB
/// cannot parse comes back as <see cref="SheetFailed"/> (with the cell and the options), never as a half-read table. Trusted code: it runs in
/// the Worker with DuckDB's Excel extension, on a copy of the person's own uploaded file.
/// </summary>
public sealed partial class SpreadsheetReader(
    IExtractUploader uploader, IOptions<ConnectorOptions> options, TimeProvider clock, ILogger<SpreadsheetReader> logger)
{
    public const long MaxRowsPerSheet = 1_048_576;

    /// <summary>The sheet names of an .xlsx workbook, in workbook order (read from the file's own index, no data is parsed).</summary>
    public static IReadOnlyList<string> ListSheets(string localPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(localPath);
            var entry = zip.GetEntry("xl/workbook.xml") ?? throw new SpreadsheetException("This is not an .xlsx workbook (xl/workbook.xml is missing).");
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            return [.. document.Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => (string?)e.Attribute("name")).OfType<string>()];
        }
        catch (InvalidDataException)
        {
            throw new SpreadsheetException("Failed to open zip for reading");
        }
    }

    /// <summary>Parses one sheet. On success the Parquet is uploaded and a new dataset version describes it.</summary>
    public async Task<SheetOutcome> ParseSheetAsync(
        TenantId tenant, string localPath, string sheet, SheetSettings settings, string createdBy, CancellationToken cancellationToken)
    {
        var work = Path.Combine(options.Value.WorkDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = await DuckDbExtractWriter.CreateAsync(work, options.Value.MemoryLimit, cancellationToken);
            SheetLayout? layout = null;
            try
            {
                // Unless the person chose a range or "no header", look for a title and multi-row headers above the table.
                if (settings.Header && string.IsNullOrWhiteSpace(settings.Range))
                {
                    layout = SheetLayoutDetector.Detect(await writer.PreviewExcelAsync(localPath, sheet, SheetLayoutDetector.PreviewRows, cancellationToken));
                }

                var excel = layout is null
                    ? new ExcelReadOptions(sheet, settings.Header, settings.Range, settings.AllVarchar, settings.IgnoreErrors)
                    : new ExcelReadOptions(
                        sheet, Header: false, Range: $"A{layout.FirstDataRow}:{ColumnLetters(layout.ColumnNames.Count - 1)}{Math.Max(layout.FirstDataRow, LastRow(localPath, sheet) ?? MaxRowsPerSheet)}",
                        settings.AllVarchar, settings.IgnoreErrors, layout.ColumnNames);
                await writer.ImportFileAsync(localPath, ExtractFileFormat.Excel, MaxRowsPerSheet, cancellationToken, excel);
            }
            catch (SpreadsheetException ex)
            {
                return SpreadsheetProblems.Classify(ex, sheet, settings);
            }

            var output = await writer.CompleteAsync(cancellationToken);
            if (output.RowCount == 0)
            {
                return SpreadsheetProblems.NoDataRows(sheet, settings);
            }

            var id = DatasetVersion.NewId();
            await uploader.UploadAsync(tenant, id, output.ParquetPath, cancellationToken);
            var version = DatasetVersion.CreateSource(id, tenant, output.Schema, output.RowCount, createdBy, clock.GetUtcNow());
            LogParsed(logger, output.RowCount, output.Schema.Columns.Count);
            return new SheetParsed(version, output.RowCount, output.Schema.Columns.Count, layout, writer.TotalRowsFlagged);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (IOException)
            {
                // Scratch space; cleaned up on the next run.
            }
        }
    }

    /// <summary>The last used row according to the sheet's own <c>dimension</c> element (read without loading the sheet), or null when it has none.</summary>
    internal static long? LastRow(string localPath, string sheet)
    {
        try
        {
            using var zip = ZipFile.OpenRead(localPath);
            XDocument workbook;
            using (var stream = (zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException()).Open())
            {
                workbook = XDocument.Load(stream);
            }

            var node = workbook.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheet" && (string?)e.Attribute("name") == sheet);
            var rid = node?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (rid is null || zip.GetEntry("xl/_rels/workbook.xml.rels") is not { } relsEntry)
            {
                return null;
            }

            XDocument rels;
            using (var stream = relsEntry.Open())
            {
                rels = XDocument.Load(stream);
            }

            var target = rels.Descendants().FirstOrDefault(e => (string?)e.Attribute("Id") == rid)?.Attribute("Target")?.Value;
            if (target is null)
            {
                return null;
            }

            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            if (zip.GetEntry(path) is not { } sheetEntry)
            {
                return null;
            }

            using var sheetStream = sheetEntry.Open();
            using var xml = System.Xml.XmlReader.Create(sheetStream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            while (xml.Read())
            {
                if (xml.NodeType == System.Xml.XmlNodeType.Element && xml.LocalName == "sheetData")
                {
                    return null; // the dimension comes before the data
                }

                if (xml.NodeType == System.Xml.XmlNodeType.Element && xml.LocalName == "dimension")
                {
                    var reference = xml.GetAttribute("ref") ?? string.Empty;
                    var digits = new string([.. reference[(reference.LastIndexOf(':') + 1)..].Where(char.IsAsciiDigit)]);
                    return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var row) ? row : null;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return null;
        }
    }

    private static string ColumnLetters(int index)
    {
        var name = string.Empty;
        for (var n = index; n >= 0; n = n / 26 - 1)
        {
            name = (char)('A' + n % 26) + name;
        }

        return name;
    }

    /// <summary>A name for the sheet's table in questions' SQL: file and sheet, lower-case letters/digits/underscores, unique among <paramref name="used"/>.</summary>
    public static string TableName(string fileName, string sheet, ISet<string> used)
    {
        static string Snake(string text)
        {
            var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
            return string.Join('_', new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        }

        var stem = Snake(Path.GetFileNameWithoutExtension(fileName));
        var name = Snake(sheet);
        var baseName = string.Join('_', new[] { stem, name }.Where(p => p.Length > 0));
        baseName = baseName.Length == 0 ? "sheet" : char.IsAsciiDigit(baseName[0]) ? "t_" + baseName : baseName;
        if (baseName.Length > 60)
        {
            baseName = baseName[..60].TrimEnd('_');
        }

        var unique = baseName;
        for (var i = 2; !used.Add(unique); i++)
        {
            unique = $"{baseName}_{i}";
        }

        return unique;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parsed a sheet with DuckDB: {Rows} rows, {Columns} columns")]
    private static partial void LogParsed(ILogger logger, long rows, int columns);
}

