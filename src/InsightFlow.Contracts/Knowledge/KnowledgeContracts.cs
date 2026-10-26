namespace InsightFlow.Contracts.Knowledge;

/// <summary>The tenant's Neo4j connection settings. The password is never returned; <see cref="HasPassword"/> only says whether one is stored.</summary>
public sealed record KnowledgeBaseConfigDto(
    string Uri,
    string User,
    string Database,
    bool UseAura,
    bool Encrypted,
    bool Enabled,
    bool HasPassword,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    DateTimeOffset? LastSeededAt,
    int LastSeedTables,
    int LastSeedColumns,
    int LastSeedRelationships);

/// <summary>Saves the connection. A null or empty <see cref="Password"/> keeps the stored one.</summary>
public sealed record SaveKnowledgeBaseConfigRequest(
    string Uri,
    string User,
    string? Password,
    string Database,
    bool UseAura,
    bool Encrypted,
    bool Enabled);

public sealed record KnowledgeTestResultDto(bool Succeeded, string Message, string? ServerAgent);

/// <summary>Counts of what is currently in the tenant's Neo4j graph.</summary>
public sealed record KnowledgeGraphSummaryDto(int Tables, int Columns, int Relationships, int Lineage);

public sealed record KnowledgeSeedResultDto(int Tables, int Columns, int Relationships, int Lineage);

// ---------------------------------------------------------------------------------------------------------------
// Definitions: the editable tables, columns and relationships that are pushed to Neo4j.
// ---------------------------------------------------------------------------------------------------------------

public sealed record KnowledgeColumnDto(Guid Id, string Name, string DataType, string? Description, bool IsPrimaryKey, bool IsNullable);

/// <summary><see cref="Source"/> is Database, Dataset or Manual; <see cref="ConnectionName"/> names the saved connection it came from, if any.</summary>
public sealed record KnowledgeTableDto(
    Guid Id,
    Guid? ConnectionId,
    string? ConnectionName,
    string? DatabaseName,
    string? SchemaName,
    string Name,
    string DisplayName,
    string? Description,
    string Source,
    bool StructureLocked,
    IReadOnlyList<KnowledgeColumnDto> Columns);

public sealed record KnowledgeRelationshipDto(
    Guid Id,
    Guid FromColumnId,
    Guid ToColumnId,
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    string Kind,
    double Confidence,
    string Reason,
    string Source,
    Guid? GroupId = null);

/// <summary>A database on a server and its description: the top of the description hierarchy (database, tables, columns).</summary>
public sealed record KnowledgeDatabaseDto(Guid? ConnectionId, string? ConnectionName, string Name, string? Description, int TableCount);

/// <summary>Writes the description of a database (by connection and name). A blank description clears it.</summary>
public sealed record SaveDatabaseDescriptionRequest(Guid? ConnectionId, string Name, string? Description);

public sealed record KnowledgeDefinitionsDto(
    IReadOnlyList<KnowledgeTableDto> Tables, IReadOnlyList<KnowledgeRelationshipDto> Relationships, IReadOnlyList<KnowledgeDatabaseDto>? Databases = null);

/// <summary>A column in a save request. A null <see cref="Id"/> adds a new column; an existing id updates it; columns left out are removed.</summary>
public sealed record SaveKnowledgeColumnRequest(Guid? Id, string Name, string DataType, string? Description, bool IsPrimaryKey, bool IsNullable);

public sealed record SaveKnowledgeTableRequest(string? SchemaName, string Name, string? Description, IReadOnlyList<SaveKnowledgeColumnRequest> Columns);

public sealed record SaveKnowledgeRelationshipRequest(Guid FromColumnId, Guid ToColumnId, string Kind, string? Reason);

/// <summary>One matching pair of a composite relationship.</summary>
public sealed record ColumnPairDto(Guid FromColumnId, Guid ToColumnId);

/// <summary>
/// One relationship made of several column pairs that must match together (a composite key). All "from" columns belong to one table and
/// all "to" columns to one table.
/// </summary>
public sealed record SaveCompositeRelationshipRequest(IReadOnlyList<ColumnPairDto> Pairs, string Kind, string? Reason);

/// <summary>Imports the given tables (discovery ids such as <c>dbo.Orders</c>) from a database on a saved connection (default: the connection's own database).</summary>
public sealed record ImportTablesRequest(Guid ConnectionId, IReadOnlyList<string> TableIds, string? Database = null);

/// <summary>What an import did. <see cref="Skipped"/> counts tables that were already in the knowledge base (their definitions are kept).</summary>
public sealed record ImportResultDto(int Tables, int Columns, int Relationships, int Skipped);

public sealed record DatabaseDescriptionSuggestionDto(Guid? ConnectionId, string Database, string Description);

public sealed record TableDescriptionSuggestionDto(Guid TableId, string TableName, string Description);

public sealed record ColumnDescriptionSuggestionDto(Guid ColumnId, string TableName, string ColumnName, string Description);

public sealed record RelationshipSuggestionDto(
    Guid FromColumnId, Guid ToColumnId, string FromLabel, string ToLabel, string Kind, double Confidence, string Reason);

/// <summary>What the AI proposes. Nothing is stored until a person accepts it (<see cref="ApplySuggestionsRequest"/>).</summary>
public sealed record KnowledgeSuggestionsDto(
    IReadOnlyList<TableDescriptionSuggestionDto> TableDescriptions,
    IReadOnlyList<ColumnDescriptionSuggestionDto> ColumnDescriptions,
    IReadOnlyList<RelationshipSuggestionDto> Relationships,
    int DroppedRelationships,
    IReadOnlyList<RelationshipRemovalDto>? Removals = null,
    IReadOnlyList<DatabaseDescriptionSuggestionDto>? DatabaseDescriptions = null);

/// <summary>The suggestions a person accepted (a subset of <see cref="KnowledgeSuggestionsDto"/>).</summary>
public sealed record ApplySuggestionsRequest(
    IReadOnlyList<TableDescriptionSuggestionDto> TableDescriptions,
    IReadOnlyList<ColumnDescriptionSuggestionDto> ColumnDescriptions,
    IReadOnlyList<RelationshipSuggestionDto> Relationships,
    IReadOnlyList<Guid>? RemoveRelationshipIds = null,
    IReadOnlyList<DatabaseDescriptionSuggestionDto>? DatabaseDescriptions = null);

/// <summary>Asks the AI to describe tables and columns and find relationships. <see cref="OnlyMissing"/> leaves existing descriptions alone.</summary>
public sealed record KnowledgeSuggestRequest(bool OnlyMissing);

// ---------------------------------------------------------------------------------------------------------------
// Source explorer: connect to a database server, browse its databases, tables and columns, then import.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>
/// A database server to connect to. <see cref="Mode"/> is <c>fields</c> (use <see cref="Host"/>, <see cref="User"/>, …) or
/// <c>connectionString</c> (use <see cref="ConnectionString"/>; only host, port, database, user, password and TLS options are
/// read from it, everything else is ignored). The password never comes back from the server.
/// </summary>
public sealed record SourceConnectionRequest(
    string Kind,
    string Mode,
    string? Name,
    string? Host,
    string? Port,
    string? Database,
    string? User,
    string? Password,
    string? ConnectionString,
    string? SslMode,
    bool TrustCertificate);

/// <summary>Result of testing a connection that has not been saved. <see cref="Databases"/> are the databases the login can see.</summary>
public sealed record SourceTestResultDto(bool Success, string Message, IReadOnlyList<string> Databases);

public sealed record SourceColumnDto(string Name, string DataType, bool IsNullable, bool IsPrimaryKey);

public sealed record SourceForeignKeyDto(string FromTable, string FromColumn, string ToTable, string ToColumn);

public sealed record SourceTableDefinitionDto(string Id, string Name, string? Schema, string Kind, IReadOnlyList<SourceColumnDto> Columns);

/// <summary>Tables with their columns and the foreign keys leaving them, read from the database catalog (no rows).</summary>
public sealed record SourceSchemaDto(IReadOnlyList<SourceTableDefinitionDto> Tables, IReadOnlyList<SourceForeignKeyDto> ForeignKeys);

/// <summary>Which columns to read for a saved connection: a database on the server and the tables in it.</summary>
public sealed record SourceDescribeRequest(string? Database, IReadOnlyList<string> TableIds);

// ---------------------------------------------------------------------------------------------------------------
// AI assistant chat: ask for a draft, then tell the assistant what to change. Nothing is stored until a person accepts.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>An existing relationship the assistant proposes to delete.</summary>
public sealed record RelationshipRemovalDto(Guid RelationshipId, string FromLabel, string ToLabel, string Kind);

/// <summary>One chat message. <see cref="Role"/> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record KnowledgeChatMessageDto(string Role, string Content);

/// <summary>
/// A turn of the assistant conversation. <see cref="Mode"/> is <c>descriptions</c> or <c>relationships</c>. With no messages the assistant
/// writes a first draft (<see cref="OnlyMissing"/> limits descriptions to items that have none). <see cref="Current"/> is the proposal
/// currently on screen, so the assistant revises it instead of starting over.
/// </summary>
public sealed record KnowledgeChatRequest(
    string Mode,
    IReadOnlyList<KnowledgeChatMessageDto> Messages,
    KnowledgeSuggestionsDto? Current,
    bool OnlyMissing,
    bool UseProcedures = true) : InsightFlow.Contracts.Agents.IUserText
{
    /// <summary>What the person typed to the assistant (the replies in the conversation are the assistant's own words).</summary>
    public IEnumerable<string> UserTexts => Messages.Where(m => m.Role == "user").Select(m => m.Content);
}

public sealed record KnowledgeChatResponse(string Reply, KnowledgeSuggestionsDto Suggestions, InsightFlow.Contracts.Agents.UsageDto? Usage = null);

/// <summary>Removes tables (with their columns and every relationship that uses them) from the knowledge base. The source is never touched.</summary>
public sealed record RemoveTablesRequest(IReadOnlyList<Guid> TableIds);

public sealed record RemoveTablesResultDto(int Tables, int Columns, int Relationships);

/// <summary>
/// What is known about the stored procedures that use the knowledge base's tables: how many were found, how many of the tables they
/// cover, and when they were last read. <see cref="Notes"/> lists databases that could not be read (and why).
/// </summary>
public sealed record KnowledgeProceduresDto(
    int Procedures, int TablesCovered, int TablesTotal, DateTimeOffset? ScannedAt, IReadOnlyList<string> Notes);
