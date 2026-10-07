using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Workspace;

/// <summary>Who is acting on the workspace: tenant, user id (token subject) and highest role.</summary>
public sealed record WorkspacePrincipal(TenantId TenantId, string UserId, TenantRole Role);

/// <summary>
/// Decides who may read or change workspace content. An interface so Developer 2 can layer per-folder ACLs and
/// sharing on top (TODO(dev2)) without touching callers; <see cref="ContentPermissionEvaluator"/> is the v1 rule set.
/// </summary>
public interface IContentPermissionEvaluator
{
    bool CanRead(Folder folder, WorkspacePrincipal principal);

    bool CanWrite(Folder folder, WorkspacePrincipal principal);
}

/// <summary>
/// v1 rules. Personal folders: owner only (admins included — personal space is private). Shared folders:
/// every role reads; Creator and TenantAdmin write. Never across tenants.
/// </summary>
public sealed class ContentPermissionEvaluator : IContentPermissionEvaluator
{
    public bool CanRead(Folder folder, WorkspacePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(principal);

        if (folder.TenantId != principal.TenantId)
        {
            return false;
        }

        return folder.Scope switch
        {
            FolderScope.Personal => IsOwner(folder, principal),
            FolderScope.Shared => true,
            _ => false,
        };
    }

    public bool CanWrite(Folder folder, WorkspacePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(principal);

        if (folder.TenantId != principal.TenantId)
        {
            return false;
        }

        return folder.Scope switch
        {
            FolderScope.Personal => IsOwner(folder, principal),
            FolderScope.Shared => principal.Role >= TenantRole.Creator,
            _ => false,
        };
    }

    private static bool IsOwner(Folder folder, WorkspacePrincipal principal) =>
        folder.OwnerUserId is not null && string.Equals(folder.OwnerUserId, principal.UserId, StringComparison.Ordinal);
}
