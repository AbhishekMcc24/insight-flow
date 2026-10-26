namespace InsightFlow.Web.Components.Ui;

/// <summary>Shared Tailwind class strings for native form controls (kept here so every page looks the same).</summary>
public static class UiStyles
{
    public const string Input =
        "block w-full rounded-control border border-line bg-white px-3 py-2 text-sm text-ink placeholder:text-ink-muted shadow-xs transition-colors hover:border-brand-300 focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-500/30 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100";

    public const string Label = "mb-1 block text-xs font-medium uppercase tracking-wide text-ink-soft dark:text-slate-400";

    public const string Badge =
        "inline-flex items-center gap-1 rounded-full bg-brand-100 px-2.5 py-0.5 text-xs font-medium text-brand-700 dark:bg-brand-900/50 dark:text-brand-200";

    /// <summary>Header cell of a data table.</summary>
    public const string TableHead = "px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-ink-soft";

    /// <summary>Body cell of a data table.</summary>
    public const string TableCell = "px-3 py-2.5 text-sm";

    /// <summary>ARIA state attributes need the text "true" or "false"; Blazor would render a bool as an empty or missing attribute.</summary>
    public static string Aria(bool value) => value ? "true" : "false";
}

/// <summary>Visual weight of a <see cref="Button"/>.</summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}
