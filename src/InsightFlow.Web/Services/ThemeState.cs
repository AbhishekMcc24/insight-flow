using Microsoft.JSInterop;

namespace InsightFlow.Web.Services;

/// <summary>Per-circuit light/dark state, so charts can re-theme when the user toggles dark mode (see wwwroot/js/theme.js).</summary>
public sealed class ThemeState(IJSRuntime js)
{
    public bool Dark { get; private set; }

    public event Action? Changed;

    /// <summary>Reads the theme applied by theme.js before first paint. Call after the first interactive render.</summary>
    public async Task InitializeAsync()
    {
        var dark = await js.InvokeAsync<bool>("insightFlowTheme.isDark");
        if (dark != Dark)
        {
            Dark = dark;
            Changed?.Invoke();
        }
    }

    public async Task ToggleAsync()
    {
        Dark = await js.InvokeAsync<bool>("insightFlowTheme.toggle");
        Changed?.Invoke();
    }
}
