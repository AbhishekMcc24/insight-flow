namespace InsightFlow.Web.Components.Ui;

/// <summary>Shared Tailwind class strings for native form controls (kept here so every page looks the same).</summary>
public static class UiStyles
{
    public const string Input =
        "block w-full rounded-md border border-slate-300 bg-white px-2.5 py-1.5 text-sm shadow-xs focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-500/30 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-900";

    public const string Label = "mb-1 block text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400";

    public const string Badge =
        "inline-flex items-center gap-1 rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300";
}

/// <summary>Visual weight of a <see cref="Button"/>.</summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}
