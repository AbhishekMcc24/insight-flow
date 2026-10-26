namespace InsightFlow.Contracts.Knowledge;

// ---------------------------------------------------------------------------------------------------------------
// Query: plain-English questions answered through the knowledge graph (find tables, plan joins, write SQL, run it).
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Step 1 (the Api, through Neo4j): which tables and joins a question needs.</summary>
/// <summary><see cref="ExtraTableIds"/> are tables already chosen (by skills, or by the table-picking agent when <see cref="ChosenByAgent"/>, in which case the word search is skipped).</summary>
public sealed record QueryContextRequest(string Question, IReadOnlyList<Guid>? ExtraTableIds = null, bool ChosenByAgent = false);

/// <summary>A table the graph picked for a question. <see cref="IsBridge"/> marks tables added only to connect others.</summary>
public sealed record QueryTableDto(Guid Id, string Name, double Score, IReadOnlyList<string> MatchedColumns, bool IsBridge);

/// <summary>One way two tables can be joined, as the graph knows it.</summary>
public sealed record QueryJoinDto(string FromTable, string FromColumn, string ToTable, string ToColumn, string Kind, double Confidence);

/// <summary>
/// What the graph found. <see cref="Fallback"/> is true when no table matched the words and the best-connected tables were offered
/// instead; <see cref="Unconnected"/> names wanted tables the graph cannot join to the others.
/// </summary>
public sealed record QueryContextDto(
    IReadOnlyList<QueryTableDto> Tables,
    IReadOnlyList<QueryJoinDto> Joins,
    IReadOnlyList<string> Keywords,
    bool Fallback,
    IReadOnlyList<string> Unconnected);

/// <summary>One earlier message of the conversation (<c>user</c> or <c>assistant</c>).</summary>
/// <summary>
/// <see cref="Data"/> is a short digest of what an answer returned (the tables used and the first rows), so a follow-up such as "show the
/// details of that payment" can use a value from an earlier result. It is sent to the model only when <c>Conversation:ShareResultRows</c> is on.
/// </summary>
public sealed record QueryTurnDto(string Role, string Content, string? Data = null);

/// <summary>Checks whether a conversation is nearing the model's context window and, when it is, summarizes it (see <see cref="ConversationCompactDto"/>).</summary>
public sealed record ConversationCompactRequest(IReadOnlyList<QueryTurnDto> Turns, bool Force = false) : InsightFlow.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => Turns.Where(t => t.Role == "user").Select(t => t.Content);
}

/// <summary>
/// <see cref="Compacted"/> is true when the conversation used at least the configured share (80%) of the model's context window: then
/// <see cref="Summary"/> replaces everything said so far. <see cref="EstimatedTokens"/> of <see cref="ContextWindow"/> shows how full it is.
/// </summary>
public sealed record ConversationCompactDto(
    bool Compacted, string? Summary, long EstimatedTokens, long Threshold, long ContextWindow, string? Model, InsightFlow.Contracts.Agents.UsageDto Usage);

/// <summary>Step 2 (the AgentService): plan and write SQL over the chosen tables. Ids are checked against the caller's own knowledge base.</summary>
public sealed record QueryPlanRequest(string Question, IReadOnlyList<Guid> TableIds, IReadOnlyList<QueryTurnDto>? History = null, IReadOnlyList<Guid>? SkillIds = null) : InsightFlow.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => [Question, .. (History ?? []).Where(t => t.Role == "user").Select(t => t.Content)];
}

/// <summary>A table as the SQL names it: <see cref="Alias"/> is the identifier used in the SQL, <see cref="Id"/> the knowledge-base table.</summary>
public sealed record QueryAliasDto(Guid Id, string Alias, string Table);

/// <summary>
/// The answer plan. <see cref="Sql"/> is one DuckDB SELECT over the aliases (null when <see cref="Clarification"/> asks the person a
/// question instead). <see cref="Joins"/> are the joins the SQL actually uses, each one a relationship from the graph.
/// </summary>
public sealed record QueryPlanDto(
    string? Explanation,
    string? Sql,
    IReadOnlyList<QueryAliasDto> Tables,
    IReadOnlyList<QueryJoinDto> Joins,
    IReadOnlyList<string> Assumptions,
    string? Clarification,
    InsightFlow.Contracts.Agents.UsageDto? Usage = null,
    double DurationMs = 0,
    IReadOnlyList<string>? SkillsApplied = null,
    InsightFlow.Contracts.Query.ChartSpecDto? Chart = null);

/// <summary>Makes sure a table has a recent copy of its rows to answer from. <see cref="Refresh"/> copies it again even if the copy is recent.</summary>
public sealed record QueryExtractRequest(bool Refresh = false);

/// <summary>
/// The copy a question is answered from. <see cref="Cached"/> is true when an existing recent copy was reused; <see cref="Truncated"/>
/// when the table has more rows than the copy limit, so answers use only its first rows.
/// </summary>
public sealed record QueryExtractDto(Guid TableId, string Table, Guid VersionId, long Rows, DateTimeOffset ExtractedAt, bool Cached, bool Truncated);

/// <summary>Runs the SQL a plan produced over the copies of the tables it names (each alias is a knowledge-base table).</summary>
public sealed record QueryRunRequest(string Sql, IReadOnlyList<QueryAliasDto> Tables);

/// <summary>The rows of a run, or the reason it failed (<see cref="Error"/> is safe to show). <see cref="Truncated"/> when the result was cut at the row limit.</summary>
public sealed record QueryRunDto(InsightFlow.Contracts.Query.QueryResult? Result, long RowCount, double DurationMs, string? Error, bool Truncated);

/// <summary>A person's verdict on an answer: right, or wrong with the reason (required when wrong).</summary>
public sealed record QueryFeedbackRequest(string Question, string? Sql, bool Right, string? Reason);

/// <summary>
/// Asks the AI to change descriptions so a question the person says was answered wrongly is answered correctly next time.
/// <see cref="TableIds"/> are the tables the wrong answer used; <see cref="ResultColumns"/> its output column names (never its values).
/// </summary>
public sealed record QueryFixRequest(string Question, string Sql, IReadOnlyList<Guid> TableIds, string Reason, IReadOnlyList<string>? ResultColumns = null) : InsightFlow.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => [Question, Reason];
}

/// <summary>
/// Step 2 of a question: an agent chooses the tables it needs. <see cref="SkillIds"/> are the skills already chosen; the tables they are
/// about are always included.
/// </summary>
public sealed record TablePickRequest(string Question, IReadOnlyList<Guid>? SkillIds = null, IReadOnlyList<QueryTurnDto>? History = null) : InsightFlow.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => [Question, .. (History ?? []).Where(t => t.Role == "user").Select(t => t.Content)];
}

public sealed record PickedTableDto(Guid Id, string Name, string Reason, bool FromSkill);

/// <summary>The tables chosen, how many tables the knowledge base has and how many the agent looked at, and what choosing cost.</summary>
public sealed record TablePickResponse(
    IReadOnlyList<PickedTableDto> Tables, int CatalogSize, int Considered, double DurationMs, InsightFlow.Contracts.Agents.UsageDto Usage);
