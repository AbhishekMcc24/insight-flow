namespace InsightFlow.Domain.Contracts.Admin;

/// <summary>Role names exchanged with the Web UI. They map to tenant roles: Viewer, Editor = Creator, SysAdmin = TenantAdmin.</summary>
public static class UserRoleNames
{
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string SysAdmin = "SysAdmin";

    public static readonly IReadOnlyList<string> All = [Viewer, Editor, SysAdmin];

    public static string Display(string role) => role switch
    {
        SysAdmin => "Sys-admin",
        _ => role,
    };
}

/// <summary>A managed user. The password hash never leaves the Api; <see cref="HasPassword"/> only says whether one is set.</summary>
public sealed record UserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string Role,
    bool IsActive,
    bool HasPassword,
    DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(string UserName, string DisplayName, string? Email, string Role, string Password);

public sealed record UpdateUserRequest(string DisplayName, string? Email, string Role, bool IsActive);

public sealed record SetPasswordRequest(string Password);

/// <summary>A navigation entry. Lists are flat; clients build the tree from <see cref="ParentId"/> and order by <see cref="Sequence"/>.</summary>
public sealed record MenuItemDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Icon,
    string? Route,
    int Sequence,
    bool IsActive,
    bool IsSystem);

public sealed record SaveMenuItemRequest(Guid? ParentId, string Name, string? Icon, string? Route, int Sequence, bool IsActive);

/// <summary>The menu entries one user may open (<c>PUT</c> replaces the whole set).</summary>
public sealed record UserMenuAccessDto(IReadOnlyList<Guid> MenuItemIds);
