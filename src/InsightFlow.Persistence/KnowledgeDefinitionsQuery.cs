using InsightFlow.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Persistence;

/// <summary>Everything stored in the knowledge base for the current tenant, in one consistent read.</summary>
public sealed record KnowledgeDefinitions(
    IReadOnlyList<KnowledgeTable> Tables,
    IReadOnlyList<KnowledgeColumn> Columns,
    IReadOnlyList<KnowledgeRelationship> Relationships,
    IReadOnlyList<KnowledgeDatabase> Databases)
{
    /// <summary>The graph that would be written to Neo4j for these definitions.</summary>
    public KnowledgeGraphPlan ToPlan() => KnowledgeGraphPlanRules.FromDefinitions(Tables, Columns, Relationships, Databases);
}

/// <summary>Reads the knowledge base through the tenant-filtered context, so a caller only ever sees their own tenant's definitions.</summary>
public static class KnowledgeDefinitionsQuery
{
    public static async Task<KnowledgeDefinitions> LoadDefinitionsAsync(this InsightFlowDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tables = await db.KnowledgeTables.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        var columns = await db.KnowledgeColumns.AsNoTracking().ToListAsync(cancellationToken);
        var relationships = await db.KnowledgeRelationships.AsNoTracking().ToListAsync(cancellationToken);
        var databases = await db.KnowledgeDatabases.AsNoTracking().ToListAsync(cancellationToken);
        return new KnowledgeDefinitions(tables, columns, relationships, databases);
    }
}
