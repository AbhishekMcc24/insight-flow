using InsightFlow.Contracts.Admin;
using InsightFlow.ServiceDefaults.Security;

namespace InsightFlow.Api.Admin;

/// <summary>
/// <c>/api/v1/admin</c>: users, menu master and per-user menu access, all Sys-admin only; and
/// <c>/api/v1/navigation</c>, the menu the signed-in caller sees in the side panel (any tenant member).
/// </summary>
internal static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapGet("/users", (AdminService admin, CancellationToken ct) => admin.ListUsersAsync(ct)).WithName("ListUsers");

        group.MapPost("/users", async (CreateUserRequest request, AdminService admin, CancellationToken ct) =>
        {
            var user = await admin.CreateUserAsync(request, ct);
            return TypedResults.Created($"/api/v1/admin/users/{user.Id}", user);
        }).WithName("CreateUser");

        group.MapPut("/users/{userId:guid}", (Guid userId, UpdateUserRequest request, AdminService admin, CancellationToken ct) =>
            admin.UpdateUserAsync(userId, request, ct)).WithName("UpdateUser");

        group.MapPut("/users/{userId:guid}/password", async (Guid userId, SetPasswordRequest request, AdminService admin, CancellationToken ct) =>
        {
            await admin.SetPasswordAsync(userId, request.Password, ct);
            return TypedResults.NoContent();
        }).WithName("SetUserPassword");

        group.MapGet("/users/{userId:guid}/menus", (Guid userId, AdminService admin, CancellationToken ct) =>
            admin.GetUserAccessAsync(userId, ct)).WithName("GetUserMenuAccess");

        group.MapPut("/users/{userId:guid}/menus", (Guid userId, UserMenuAccessDto request, AdminService admin, CancellationToken ct) =>
            admin.SetUserAccessAsync(userId, request, ct)).WithName("SetUserMenuAccess");

        group.MapGet("/menus", (AdminService admin, CancellationToken ct) => admin.ListMenusAsync(ct)).WithName("ListMenus");

        group.MapPost("/menus", async (SaveMenuItemRequest request, AdminService admin, CancellationToken ct) =>
        {
            var item = await admin.CreateMenuAsync(request, ct);
            return TypedResults.Created($"/api/v1/admin/menus/{item.Id}", item);
        }).WithName("CreateMenu");

        group.MapPut("/menus/{menuId:guid}", (Guid menuId, SaveMenuItemRequest request, AdminService admin, CancellationToken ct) =>
            admin.UpdateMenuAsync(menuId, request, ct)).WithName("UpdateMenu");

        group.MapDelete("/menus/{menuId:guid}", async (Guid menuId, AdminService admin, CancellationToken ct) =>
        {
            await admin.DeleteMenuAsync(menuId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteMenu");

        return group;
    }

    public static RouteGroupBuilder MapNavigationEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);
        group.MapGet("/", (AdminService admin, CancellationToken ct) => admin.GetMyNavigationAsync(ct)).WithName("GetMyNavigation");
        return group;
    }
}
