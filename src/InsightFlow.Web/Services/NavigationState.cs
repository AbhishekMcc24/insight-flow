using InsightFlow.Contracts.Admin;

namespace InsightFlow.Web.Services;

/// <summary>
/// Per-circuit copy of the signed-in user's side-panel menu. Menu Master and User Form Access call
/// <see cref="RefreshAsync"/> after a change so the side panel updates without a page reload.
/// </summary>
public sealed class NavigationState(AdminApiClient admin)
{
    public IReadOnlyList<MenuItemDto>? Items { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            Items = await admin.GetMyNavigationAsync(cancellationToken);
            Error = null;
        }
        catch (ServiceCallException ex)
        {
            Items ??= [];
            Error = ex.Message;
        }

        Changed?.Invoke();
    }
}
