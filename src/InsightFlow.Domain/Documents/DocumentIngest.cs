using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Documents;

/// <summary>Where an ingestion run is.</summary>
public enum IngestStatus
{
    Pending,
    Running,

    /// <summary>Stopped on a problem DuckDB could not get past; the person has to choose what to do (see <see cref="IngestProblem"/>).</summary>
    NeedsInput,

    Succeeded,
    Failed,
}

/// <summary>Where one step of the pipeline is.</summary>
public enum IngestStepState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>One step of the pipeline as the person sees it, with a short detail such as "3 of 5 sheets".</summary>
public sealed record IngestStep(string Key, string Title, IngestStepState State, string? Detail = null);

/// <summary>The fixed steps of the spreadsheet pipeline, in order.</summary>
public static class IngestSteps
{
    public const string Queued = "queued";
    public const string Open = "open";
    public const string Parse = "parse";
    public const string Save = "save";
    public const string Ready = "ready";

    public static IReadOnlyList<IngestStep> Initial() =>
    [
        new(Queued, "Queued", IngestStepState.Done),
        new(Open, "Opening the workbooks and finding the sheets", IngestStepState.Pending),
        new(Parse, "Parsing each sheet with DuckDB", IngestStepState.Pending),
        new(Save, "Saving each sheet as a table (Parquet)", IngestStepState.Pending),
        new(Ready, "Ready for questions", IngestStepState.Pending),
    ];
}

/// <summary>One thing the person can choose to get past a problem. <see cref="NeedsText"/> asks for a value too (a cell range).</summary>
public sealed record ProblemOption(string Key, string Label, bool NeedsText = false, string? TextHint = null);

/// <summary>
/// A part of a workbook DuckDB could not parse. It says where (file, sheet, cell), what went wrong in plain words, DuckDB's own message,
/// and the ways forward. The run waits until every problem has an answer (<see cref="Chosen"/>).
/// </summary>
public sealed record IngestProblem(
    Guid Id,
    Guid ItemId,
    string FileName,
    string? Sheet,
    string Kind,
    string Message,
    string? Technical,
    IReadOnlyList<ProblemOption> Options,
    string? Chosen = null,
    string? Text = null)
{
    public bool IsResolved => Chosen is not null;
}

/// <summary>The choices a person can make for a sheet, remembered between attempts.</summary>
public static class ProblemChoices
{
    public const string AsText = "as_text";
    public const string IgnoreErrors = "ignore_errors";
    public const string Range = "range";
    public const string NoHeader = "no_header";
    public const string SkipSheet = "skip_sheet";
    public const string SkipFile = "skip_file";
    public const string Retry = "retry";
}

/// <summary>
/// One request to prepare uploaded Excel files for questions. The Worker opens each workbook, parses every sheet with DuckDB and saves it as a
/// table (Parquet). When DuckDB cannot parse something the run stops in <see cref="IngestStatus.NeedsInput"/> with the problems; once they are
/// answered it continues with the sheets that are not done yet.
/// </summary>
public sealed class DocumentIngestRun : ITenantOwned
{
    public const int MaxFiles = 50;

    private DocumentIngestRun()
    {
        RequestedBy = string.Empty;
        ItemIds = [];
        Steps = [];
        Problems = [];
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string RequestedBy { get; private init; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IngestStatus Status { get; private set; }

    /// <summary>The workspace items (uploaded Excel files) chosen for this run.</summary>
    public Guid[] ItemIds { get; private init; }

    public int SheetCount { get; private set; }

    public long RowCount { get; private set; }

    public string? Error { get; private set; }

    public IReadOnlyList<IngestStep> Steps { get; private set; }

    public IReadOnlyList<IngestProblem> Problems { get; private set; }

    public static DocumentIngestRun Create(TenantId tenant, string requestedBy, IEnumerable<Guid> itemIds, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A run needs a tenant.");
        }

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length is 0 or > MaxFiles)
        {
            throw new DomainRuleException("invalid_selection", $"Choose between 1 and {MaxFiles} files.");
        }

        return new DocumentIngestRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            RequestedBy = requestedBy,
            RequestedAt = now,
            Status = IngestStatus.Pending,
            ItemIds = ids,
            Steps = IngestSteps.Initial(),
        };
    }

    public void Start(DateTimeOffset now)
    {
        Status = IngestStatus.Running;
        StartedAt ??= now;
    }

    /// <summary>Sets the state (and detail) of one step; earlier steps still pending are marked done so the list always reads top to bottom.</summary>
    public void SetStep(string key, IngestStepState state, string? detail = null)
    {
        var index = Steps.ToList().FindIndex(s => s.Key == key);
        if (index < 0)
        {
            throw new DomainRuleException("unknown_step", $"There is no step '{key}'.");
        }

        Steps = [.. Steps.Select((s, i) => i == index
            ? s with { State = state, Detail = detail }
            : i < index && s.State is IngestStepState.Pending or IngestStepState.Running ? s with { State = IngestStepState.Done } : s)];
    }

    /// <summary>Stops the run for the person to decide. Problems already answered stay answered.</summary>
    public void NeedInput(IEnumerable<IngestProblem> problems)
    {
        Problems = [.. problems];
        Status = IngestStatus.NeedsInput;
    }

    /// <summary>Records the person's answer to one problem and, when every problem has one, queues the run to continue.</summary>
    public void Resolve(Guid problemId, string optionKey, string? text, DateTimeOffset now)
    {
        _ = now;
        var problem = Problems.FirstOrDefault(p => p.Id == problemId) ?? throw new DomainRuleException("unknown_problem", "That problem is not part of this run.");
        var option = problem.Options.FirstOrDefault(o => o.Key == optionKey) ?? throw new DomainRuleException("unknown_option", "That choice is not offered for this problem.");
        var value = text?.Trim();
        if (option.NeedsText && string.IsNullOrEmpty(value))
        {
            throw new DomainRuleException("text_required", $"“{option.Label}” needs a value.");
        }

        Problems = [.. Problems.Select(p => p.Id == problemId ? p with { Chosen = optionKey, Text = option.NeedsText ? value : null } : p)];
        if (Status == IngestStatus.NeedsInput && Problems.All(p => p.IsResolved))
        {
            Status = IngestStatus.Pending;
        }
    }

    public void Succeed(int sheets, long rows, string detail, DateTimeOffset now)
    {
        SheetCount = sheets;
        RowCount = rows;
        SetStep(IngestSteps.Ready, IngestStepState.Done, detail);
        Status = IngestStatus.Succeeded;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Error = error.Length > 1_000 ? error[..1_000] : error;
        Steps = [.. Steps.Select(s => s.State == IngestStepState.Running ? s with { State = IngestStepState.Failed } : s)];
        Status = IngestStatus.Failed;
        CompletedAt = now;
    }
}

/// <summary>What happened to one sheet.</summary>
public enum IndexedSheetStatus
{
    Ready,

    /// <summary>Left out on purpose: empty, or the person chose to skip it.</summary>
    Skipped,
}

/// <summary>
/// One sheet of an uploaded workbook, saved as a table (Parquet extract <see cref="VersionId"/>) that questions are answered from.
/// Preparing the file again replaces its sheets.
/// </summary>
public sealed class IndexedSheet : ITenantOwned
{
    public const int MaxNameLength = 200;

    private IndexedSheet()
    {
        FileName = string.Empty;
        SheetName = string.Empty;
        TableName = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid ContentItemId { get; private init; }

    public Guid StoredFileId { get; private init; }

    public string FileName { get; private init; }

    public string SheetName { get; private init; }

    /// <summary>The name the table has in questions' SQL (lower-case letters, digits and underscores).</summary>
    public string TableName { get; private init; }

    public Guid? VersionId { get; private init; }

    public long Rows { get; private init; }

    public int Columns { get; private init; }

    public IndexedSheetStatus Status { get; private init; }

    /// <summary>How the sheet was read when that is not the default (for example "as text", "range A3:F200") or why it was skipped.</summary>
    public string? Note { get; private init; }

    public Guid RunId { get; private init; }

    public DateTimeOffset IndexedAt { get; private init; }

    public static IndexedSheet Ready(
        TenantId tenant, Guid contentItemId, Guid storedFileId, string fileName, string sheetName, string tableName, Guid versionId, long rows, int columns, string? note, Guid runId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            ContentItemId = contentItemId,
            StoredFileId = storedFileId,
            FileName = fileName,
            SheetName = sheetName,
            TableName = tableName,
            VersionId = versionId,
            Rows = rows,
            Columns = columns,
            Status = IndexedSheetStatus.Ready,
            Note = note,
            RunId = runId,
            IndexedAt = now,
        };

    public static IndexedSheet Skipped(
        TenantId tenant, Guid contentItemId, Guid storedFileId, string fileName, string sheetName, string note, Guid runId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            ContentItemId = contentItemId,
            StoredFileId = storedFileId,
            FileName = fileName,
            SheetName = sheetName,
            TableName = string.Empty,
            Status = IndexedSheetStatus.Skipped,
            Note = note,
            RunId = runId,
            IndexedAt = now,
        };
}
