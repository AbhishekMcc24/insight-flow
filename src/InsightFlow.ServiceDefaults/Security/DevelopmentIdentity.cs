namespace InsightFlow.ServiceDefaults.Security;

/// <summary>
/// The seeded local-development tenant and user ("Contoso Retail"). Used by the development auth handler and the
/// development seeder only; never trusted outside the Development environment.
/// </summary>
public static class DevelopmentIdentity
{
    public static readonly Guid TenantId = Guid.Parse("c0a7050f-0000-4000-8000-00000000c0de");

    public const string TenantName = "Contoso Retail";

    public const string UserId = "dev-user";

    public const string UserName = "Dev User";

    public static readonly IReadOnlyList<string> Roles = [InsightFlowRoles.Creator, InsightFlowRoles.TenantAdmin];
}
