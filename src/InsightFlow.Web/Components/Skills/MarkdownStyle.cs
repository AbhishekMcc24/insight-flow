namespace InsightFlow.Web.Components.Skills;

/// <summary>Tailwind classes that style the HTML <see cref="SimpleMarkdown"/> produces (headings, lists, code), shared by every place a skill's instructions are shown.</summary>
public static class MarkdownStyle
{
    public const string Class =
        "max-h-96 min-h-24 overflow-auto rounded-xl border border-line bg-slate-50 p-4 text-sm leading-relaxed text-ink dark:border-slate-700 dark:bg-slate-950 dark:text-slate-100 " +
        "[&_h1]:mb-2 [&_h1]:text-lg [&_h1]:font-bold [&_h2]:mb-2 [&_h2]:text-base [&_h2]:font-bold [&_h3]:mb-1 [&_h3]:font-semibold [&_p]:mb-3 " +
        "[&_ol]:mb-3 [&_ol]:list-decimal [&_ol]:pl-6 [&_ul]:mb-3 [&_ul]:list-disc [&_ul]:pl-6 [&_li]:mb-1 [&_code]:rounded [&_code]:bg-slate-200 [&_code]:px-1 [&_code]:font-mono [&_code]:text-xs " +
        "dark:[&_code]:bg-slate-800 [&_pre]:mb-3 [&_pre]:overflow-auto [&_pre]:rounded-lg [&_pre]:bg-slate-900 [&_pre]:p-3 [&_pre]:text-slate-100 [&_pre_code]:bg-transparent " +
        "[&_blockquote]:mb-3 [&_blockquote]:border-l-4 [&_blockquote]:border-brand-300 [&_blockquote]:pl-3 [&_blockquote]:text-ink-soft [&_a]:text-brand-700 [&_a]:underline";
}
