using System.Text;
using System.Text.RegularExpressions;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Knowledge;

/// <summary>
/// The text of a stored procedure that touches knowledge-base tables, kept so the AI can learn how the tables are really used (which
/// columns are filtered, how tables are joined, what is summed) before it writes descriptions. It is code, not row data, and it is
/// only ever read from the tenant's own database. Replaced as a whole by every scan.
/// </summary>
public sealed class KnowledgeProcedure : ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxDefinitionLength = 20_000;

    private KnowledgeProcedure()
    {
        Name = string.Empty;
        Definition = string.Empty;
        TableIds = [];
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid ConnectionId { get; private init; }

    public string DatabaseName { get; private init; } = string.Empty;

    public string? SchemaName { get; private init; }

    public string Name { get; private init; }

    /// <summary>The procedure's source, cut at <see cref="MaxDefinitionLength"/> characters.</summary>
    public string Definition { get; private init; }

    /// <summary>The knowledge-base tables this procedure uses.</summary>
    public Guid[] TableIds { get; private init; }

    public DateTimeOffset ScannedAt { get; private init; }

    public string DisplayName => string.IsNullOrEmpty(SchemaName) ? Name : $"{SchemaName}.{Name}";

    public static KnowledgeProcedure Create(
        TenantId tenant, Guid connectionId, string databaseName, string? schemaName, string name, string definition, IEnumerable<Guid> tableIds, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A procedure needs a tenant.");
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_procedure_name", $"A procedure name must be 1–{MaxNameLength} characters.");
        }

        var text = definition ?? string.Empty;
        return new KnowledgeProcedure
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            ConnectionId = connectionId,
            DatabaseName = databaseName,
            SchemaName = string.IsNullOrWhiteSpace(schemaName) ? null : schemaName.Trim(),
            Name = trimmed,
            Definition = text.Length > MaxDefinitionLength ? text[..MaxDefinitionLength] : text,
            TableIds = [.. tableIds.Distinct()],
            ScannedAt = now,
        };
    }
}

/// <summary>A knowledge-base table as the procedure matcher sees it.</summary>
public sealed record ProcedureTableRef(Guid Id, string? Database, string? Schema, string Name);

/// <summary>
/// Works out which knowledge-base tables a stored procedure uses, and which lines of it matter for one table. Pure text work (comments
/// are ignored, names may be bracketed, qualified by schema or database), so it also finds tables used inside dynamic SQL strings.
/// </summary>
public static class ProcedureUsage
{
    public const int ContextLines = 4;

    private static readonly Regex BlockComments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex LineComments = new(@"--[^\r\n]*", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>The ids of the tables <paramref name="definition"/> mentions. <paramref name="procedureDatabase"/> is where the procedure lives.</summary>
    public static IReadOnlyList<Guid> TablesUsed(string definition, string procedureDatabase, IReadOnlyList<ProcedureTableRef> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var text = StripComments(definition);
        return [.. tables.Where(t => PatternFor(t, procedureDatabase).IsMatch(text)).Select(t => t.Id)];
    }

    /// <summary>
    /// The parts of the procedure that mention <paramref name="table"/>: each matching line with a few lines around it, merged, in order,
    /// cut at <paramref name="maxChars"/>. Comments are kept out, since they are not what the procedure does.
    /// </summary>
    public static string Excerpt(string definition, string procedureDatabase, ProcedureTableRef table, int maxChars)
    {
        var lines = StripComments(definition).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var pattern = PatternFor(table, procedureDatabase);
        var keep = new SortedSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i]))
            {
                continue;
            }

            for (var j = Math.Max(0, i - ContextLines); j <= Math.Min(lines.Count - 1, i + ContextLines); j++)
            {
                keep.Add(j);
            }
        }

        var sb = new StringBuilder();
        var previous = -2;
        foreach (var index in keep)
        {
            if (sb.Length >= maxChars)
            {
                break;
            }

            if (index != previous + 1 && sb.Length > 0)
            {
                sb.AppendLine("…");
            }

            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                sb.AppendLine(lines[index].TrimEnd());
            }

            previous = index;
        }

        var result = sb.ToString().TrimEnd();
        return result.Length > maxChars ? result[..maxChars] + "…" : result;
    }

    internal static string StripComments(string? definition)
    {
        var text = definition ?? string.Empty;
        return LineComments.Replace(BlockComments.Replace(text, " "), string.Empty);
    }

    /// <summary>
    /// A table in the procedure's own database may be written with or without its schema; a table in another database only when the
    /// database is named (<c>other.dbo.table</c> or <c>other..table</c>), so a same-named table elsewhere is not mistaken for it.
    /// </summary>
    internal static Regex PatternFor(ProcedureTableRef table, string procedureDatabase)
    {
        static string Part(string value) => $@"\[?{Regex.Escape(value)}\]?";
        var name = Part(table.Name);
        var schema = string.IsNullOrEmpty(table.Schema) ? string.Empty : $@"(?:{Part(table.Schema)}\s*\.\s*)";
        var sameDatabase = string.IsNullOrEmpty(table.Database) || string.Equals(table.Database, procedureDatabase, StringComparison.OrdinalIgnoreCase);
        var qualified = sameDatabase
            ? $@"(?:{Part(table.Database ?? procedureDatabase)}\s*\.\s*(?:{Part(table.Schema ?? "dbo")}\s*\.\s*|\.\s*))?{schema}?{name}"
            : $@"{Part(table.Database!)}\s*\.\s*(?:{Part(table.Schema ?? "dbo")}\s*\.\s*|\.\s*){name}";
        return new Regex($@"(?<![\w@#$.\]]){qualified}(?![\w$])", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    }
}

/// <summary>
/// A person's verdict on an answer to a plain-English question: right, or wrong with the reason. Kept so the knowledge base can be
/// improved from real use and so answers can be checked against past questions later.
/// </summary>
public sealed class QueryFeedback : ITenantOwned
{
    public const int MaxQuestionLength = 2_000;
    public const int MaxSqlLength = 20_000;
    public const int MaxReasonLength = 1_000;

    private QueryFeedback()
    {
        Question = string.Empty;
        UserId = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string UserId { get; private init; }

    public string Question { get; private init; }

    public string? Sql { get; private init; }

    public bool IsRight { get; private init; }

    public string? Reason { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static QueryFeedback Create(TenantId tenant, string userId, string question, string? sql, bool isRight, string? reason, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "Feedback needs a tenant.");
        }

        var q = question?.Trim() ?? string.Empty;
        if (q.Length is 0 or > MaxQuestionLength)
        {
            throw new DomainRuleException("invalid_question", $"A question must be 1–{MaxQuestionLength} characters.");
        }

        var why = reason?.Trim();
        if (!isRight && string.IsNullOrEmpty(why))
        {
            throw new DomainRuleException("reason_required", "Say what was wrong, so the descriptions can be fixed.");
        }

        return new QueryFeedback
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            UserId = userId,
            Question = q,
            Sql = string.IsNullOrWhiteSpace(sql) ? null : sql.Length > MaxSqlLength ? sql[..MaxSqlLength] : sql,
            IsRight = isRight,
            Reason = string.IsNullOrEmpty(why) ? null : why.Length > MaxReasonLength ? why[..MaxReasonLength] : why,
            CreatedAt = now,
        };
    }
}
