using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Persistence;

/// <summary>
/// The tenant every query and write of a <see cref="InsightFlowDbContext"/> is scoped to. Request hosts resolve it
/// from the token claim; background jobs set it per job; migrations and seeding run as <see cref="System"/>.
/// </summary>
public interface ICurrentTenant
{
    /// <summary>The tenant in scope, or null when none (queries then return nothing and writes are refused).</summary>
    TenantId? TenantId { get; }

    /// <summary>True only for trusted system work (migrations, seeding) that may write rows of any tenant.</summary>
    bool IsSystem { get; }
}

/// <summary>Resolves the tenant lazily from a delegate (used by request hosts to adapt <c>ITenantContext</c>).</summary>
public sealed class DelegateCurrentTenant(Func<TenantId?> resolve) : ICurrentTenant
{
    public TenantId? TenantId => resolve();

    public bool IsSystem => false;
}

/// <summary>A fixed tenant, for background jobs and tests.</summary>
public sealed class FixedCurrentTenant(TenantId tenantId) : ICurrentTenant
{
    public TenantId? TenantId => tenantId;

    public bool IsSystem => false;
}

/// <summary>Trusted system scope for migrations and seeding. Never register it in a request-serving host.</summary>
public sealed class SystemCurrentTenant : ICurrentTenant
{
    public static SystemCurrentTenant Instance { get; } = new();

    public TenantId? TenantId => null;

    public bool IsSystem => true;
}

/// <summary>Thrown when a write would touch a row that belongs to a tenant other than the one in scope.</summary>
public sealed class TenantIsolationException : InvalidOperationException
{
    public TenantIsolationException()
    {
    }

    public TenantIsolationException(string message)
        : base(message)
    {
    }

    public TenantIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
