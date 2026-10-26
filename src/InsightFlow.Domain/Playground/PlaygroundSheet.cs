using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Playground;

/// <summary>
/// One sheet of the Playground: a free canvas a person lays out with text, shapes, charts and tables, like a sheet in a workbook. Sheets belong to
/// the person who made them. What is on the canvas is kept as one JSON document (<see cref="ElementsJson"/>) that only the Playground reads, so
/// moving, resizing and styling elements never needs a schema change.
/// </summary>
public sealed class PlaygroundSheet : ITenantOwned
{
    public const int MaxNameLength = 60;

    /// <summary>The largest canvas document that is stored (charts and tables carry a copy of their rows).</summary>
    public const int MaxElementsJsonLength = 8_000_000;

    private PlaygroundSheet()
    {
        Name = string.Empty;
        OwnerId = string.Empty;
        ElementsJson = "[]";
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    /// <summary>The user name of the person who owns the sheet (lower-case).</summary>
    public string OwnerId { get; private init; }

    public string Name { get; private set; }

    /// <summary>Position among the owner's sheets (the order of the tabs).</summary>
    public int Sequence { get; private set; }

    public string ElementsJson { get; private set; }

    /// <summary>How many elements <see cref="ElementsJson"/> holds, kept so a list of sheets does not have to read every canvas.</summary>
    public int ElementCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PlaygroundSheet Create(TenantId tenant, string ownerId, string name, int sequence, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A sheet needs a tenant.");
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new DomainRuleException("owner_required", "A sheet needs an owner.");
        }

        var sheet = new PlaygroundSheet { Id = Guid.CreateVersion7(), TenantId = tenant, OwnerId = ownerId.Trim().ToLowerInvariant(), Sequence = sequence, CreatedAt = now };
        sheet.Rename(name, now);
        return sheet;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_sheet_name", $"A sheet name must be 1–{MaxNameLength} characters.");
        }

        Name = trimmed;
        UpdatedAt = now;
    }

    public void MoveTo(int sequence, DateTimeOffset now)
    {
        Sequence = sequence;
        UpdatedAt = now;
    }

    public void SetElements(string json, int count, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || json[0] != '[')
        {
            throw new DomainRuleException("invalid_sheet_content", "The canvas content must be a list of elements.");
        }

        if (json.Length > MaxElementsJsonLength)
        {
            throw new DomainRuleException("sheet_too_large", "This sheet holds too much (many large tables or images). Remove some elements and try again.");
        }

        ElementsJson = json;
        ElementCount = count;
        UpdatedAt = now;
    }
}
