using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Admin;

/// <summary>
/// A person who can use a tenant, as managed from the User Config screen. <see cref="UserName"/> is the same
/// value the identity token carries as its user id, which is how menu access is matched to the signed-in caller.
/// The role reuses <see cref="TenantRole"/> so authorization policies stay in one place:
/// Viewer = Viewer, Editor = Creator, Sys-admin = TenantAdmin.
/// </summary>
public sealed class AppUser : ITenantOwned
{
    public const int MaxUserNameLength = 100;
    public const int MaxDisplayNameLength = 200;
    public const int MaxEmailLength = 256;

    private AppUser()
    {
        UserName = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    /// <summary>Sign-in name, stored lower-case and unique per tenant.</summary>
    public string UserName { get; private init; }

    public string DisplayName { get; private set; }

    public string? Email { get; private set; }

    public TenantRole Role { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Opaque password hash (never the password). Null when the identity provider owns the credential.</summary>
    public string? PasswordHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static bool IsAssignableRole(TenantRole role) => role is TenantRole.Viewer or TenantRole.Creator or TenantRole.TenantAdmin;

    public static AppUser Create(TenantId tenant, string userName, string displayName, string? email, TenantRole role, string? passwordHash, DateTimeOffset createdAt)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A user needs a tenant.");
        }

        return new AppUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            UserName = NormalizeUserName(userName),
            DisplayName = NormalizeDisplayName(displayName),
            Email = NormalizeEmail(email),
            Role = RequireRole(role),
            IsActive = true,
            PasswordHash = passwordHash,
            CreatedAt = createdAt,
        };
    }

    public void Update(string displayName, string? email, TenantRole role, bool isActive)
    {
        DisplayName = NormalizeDisplayName(displayName);
        Email = NormalizeEmail(email);
        Role = RequireRole(role);
        IsActive = isActive;
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainRuleException("invalid_password", "A password hash is required.");
        }

        PasswordHash = passwordHash;
    }

    public static string NormalizeUserName(string? userName)
    {
        var value = userName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length is 0 or > MaxUserNameLength || !value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
        {
            throw new DomainRuleException(
                "invalid_user_name",
                $"A user name must be 1–{MaxUserNameLength} characters: letters, digits, '.', '_', '-' or '@'.");
        }

        return value;
    }

    private static string NormalizeDisplayName(string? displayName)
    {
        var value = displayName?.Trim() ?? string.Empty;
        return value.Length is 0 or > MaxDisplayNameLength
            ? throw new DomainRuleException("invalid_display_name", $"A display name must be 1–{MaxDisplayNameLength} characters.")
            : value;
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > MaxEmailLength || !value.Contains('@', StringComparison.Ordinal)
            ? throw new DomainRuleException("invalid_email", "That email address is not valid.")
            : value;
    }

    private static TenantRole RequireRole(TenantRole role) =>
        IsAssignableRole(role) ? role : throw new DomainRuleException("invalid_role", "A user can be a Viewer, Editor or Sys-admin.");
}
