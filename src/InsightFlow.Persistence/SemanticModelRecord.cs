using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Persistence;

/// <summary>The JSONB body of a stored semantic model (everything except identity and name).</summary>
public sealed record SemanticModelDefinition(
    IReadOnlyList<ModelTable> Tables,
    IReadOnlyList<Relationship> Relationships,
    IReadOnlyList<CalculatedMeasure> Measures);

/// <summary>
/// Storage shape of a <see cref="SemanticModel"/>: identity columns plus one JSONB definition. Keeping a separate
/// record decouples the table layout from the domain record, so the model can evolve without schema migrations.
/// </summary>
public sealed class SemanticModelRecord : ITenantOwned
{
    private SemanticModelRecord()
    {
        Name = string.Empty;
        Definition = new SemanticModelDefinition([], [], []);
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string Name { get; private set; }

    public SemanticModelDefinition Definition { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SemanticModelRecord FromDomain(SemanticModel model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new SemanticModelRecord
        {
            Id = model.Id,
            TenantId = model.TenantId,
            Name = model.Name,
            Definition = new SemanticModelDefinition(model.Tables, model.Relationships, model.Measures),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(SemanticModel model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Id != Id || model.TenantId != TenantId)
        {
            throw new InvalidOperationException("A semantic model record can only be updated with the same model.");
        }

        Name = model.Name;
        Definition = new SemanticModelDefinition(model.Tables, model.Relationships, model.Measures);
        UpdatedAt = now;
    }

    public SemanticModel ToDomain() =>
        new(Id, TenantId, Name, Definition.Tables, Definition.Relationships, Definition.Measures);
}
