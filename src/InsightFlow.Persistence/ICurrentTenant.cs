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

/// <summary>
/// Tenant scope for background jobs: each DI scope (one job execution, one claimed run) enters exactly one tenant —
/// or the system scope for cross-tenant queue work — and cannot switch afterwards. Unentered scopes see nothing.
/// </summary>
public sealed class JobCurrentTenant : ICurrentTenant
{
    private bool _entered;

    public TenantId? TenantId { get; private set; }

    public bool IsSystem { get; private set; }

    public void Enter(TenantId tenant)
    {
        EnsureNotEntered();
        TenantId = tenant;
    }

    /// <summary>Only for queue bookkeeping across tenants (claiming runs). Do not load or write tenant content here.</summary>
    public void EnterSystem()
    {
        EnsureNotEntered();
        IsSystem = true;
    }

    private void EnsureNotEntered()
    {
        if (_entered)
        {
            throw new InvalidOperationException("A job scope can only enter one tenant; create a new DI scope instead.");
        }

        _entered = true;
    }
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
