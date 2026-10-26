using InsightFlow.Contracts.Knowledge;
using InsightFlow.Contracts.Query;

namespace InsightFlow.Contracts.Documents;

// ---------------------------------------------------------------------------------------------------------------
// Spreadsheets: choose uploaded Excel files, prepare them for questions (DuckDB parses every sheet), resolve problems, ask.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>
/// An uploaded Excel file that can be prepared. <see cref="State"/> is NotPrepared, Preparing, NeedsInput, Ready or Skipped.
/// <see cref="Supported"/> is false for old .xls files, which DuckDB cannot read.
/// </summary>
public sealed record SpreadsheetFileDto(
    Guid ItemId, string Name, string FolderPath, long SizeBytes, bool Supported, string State, int Sheets, long Rows, DateTimeOffset? PreparedAt, string? Note);

public sealed record IngestStepDto(string Key, string Title, string State, string? Detail);

public sealed record ProblemOptionDto(string Key, string Label, bool NeedsText, string? TextHint);

/// <summary>A part of a workbook DuckDB could not parse: where, what, DuckDB's own message, and what the person can choose.</summary>
public sealed record IngestProblemDto(
    Guid Id,
    Guid ItemId,
    string FileName,
    string? Sheet,
    string Kind,
    string Message,
    string? Technical,
    IReadOnlyList<ProblemOptionDto> Options,
    string? Chosen,
    string? Text);

/// <summary>One sheet of a run: the table it became (<see cref="Table"/>), or why it was skipped.</summary>
public sealed record IngestSheetDto(string FileName, string Sheet, string? Table, string Status, long Rows, int Columns, string? Note);

/// <summary><see cref="Status"/> is Pending, Running, NeedsInput, Succeeded or Failed.</summary>
public sealed record IngestRunDto(
    Guid Id,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> Files,
    IReadOnlyList<IngestStepDto> Steps,
    IReadOnlyList<IngestProblemDto> Problems,
    IReadOnlyList<IngestSheetDto> Sheets,
    int TableCount,
    long RowCount,
    string? Error);

public sealed record StartIngestRequest(IReadOnlyList<Guid> ItemIds);

/// <summary>The person's answer to a problem: one of its option keys, and the value when the option asks for one (a cell range).</summary>
public sealed record ResolveProblemRequest(string Option, string? Text);

/// <summary>A table the spreadsheets made available to questions.</summary>
public sealed record SheetTableDto(string Table, string File, string Sheet, long Rows, int Columns, string? Note);

public sealed record DocumentSummaryDto(int Files, int TableCount, long Rows, IReadOnlyList<SheetTableDto> Tables);

/// <summary>A question about the prepared spreadsheets, with earlier turns for follow-ups.</summary>
public sealed record DocumentAskRequest(string Question, IReadOnlyList<QueryTurnDto>? History = null) : InsightFlow.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => [Question];
}

/// <summary>
/// The answer: the SQL (one DuckDB SELECT over the sheets' tables), what it means, the rows it returned, or a question back
/// (<see cref="Clarification"/>) or the reason it could not be answered (<see cref="Error"/>).
/// </summary>
public sealed record DocumentAnswerDto(
    string? Explanation,
    string? Sql,
    IReadOnlyList<string> Assumptions,
    string? Clarification,
    QueryResult? Result,
    long RowCount,
    bool Truncated,
    string? Error,
    IReadOnlyList<string> Tables,
    QueryResult? Evidence = null,
    long EvidenceRows = 0,
    InsightFlow.Contracts.Agents.UsageDto? Usage = null,
    InsightFlow.Contracts.Query.ChartSpecDto? Chart = null);
