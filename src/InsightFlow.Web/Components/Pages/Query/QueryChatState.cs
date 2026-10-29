using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Contracts.Skills;

namespace InsightFlow.Web.Components.Pages.Query;

/// <summary>One step shown while a question is being worked on.</summary>
public sealed class QueryStep(string text)
{
    public string Text { get; set; } = text;

    public bool Done { get; set; }
}

/// <summary>One question and everything that happened for it.</summary>
public sealed class QueryExchange(string question)
{
    public string Question { get; } = question;

    public DateTime At { get; } = DateTime.Now;

    /// <summary>Set when the conversation was summarized just before this question was answered.</summary>
    public ConversationCompactDto? Compaction { get; set; }

    public List<QueryStep> Steps { get; } = [];

    public QueryContextDto? Context { get; set; }

    public bool Running { get; set; } = true;

    /// <summary>The steps of how the answer was produced, with time, tokens and cost (shown when the answer is done).</summary>
    public List<ReportStep> Report { get; } = [];

    public List<PickedSkillDto> Skills { get; } = [];

    public QueryPlanDto? Plan { get; set; }

    public List<QueryExtractDto> Extracts { get; } = [];

    public QueryRunDto? Run { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// The Query chat, kept for the length of the person's session (one per browser connection) instead of inside the page, so leaving the page and
/// coming back finds the conversation as it was, including an answer that was still being worked on. A browser refresh starts a new session
/// and so a new, empty chat. <see cref="Changed"/> lets whichever page instance is on screen redraw when an answer progresses.
/// </summary>
public sealed class QueryChatState
{
    public List<QueryExchange> Thread { get; } = [];

    /// <summary>The summary that stands for the exchanges in <see cref="Summarized"/> after the conversation was compacted.</summary>
    public string? Summary { get; set; }

    public HashSet<QueryExchange> Summarized { get; } = [];

    public ConversationCompactDto? Context { get; set; }

    /// <summary>What has been typed in the question box but not sent.</summary>
    public string Draft { get; set; } = string.Empty;

    /// <summary>True while a question is being answered.</summary>
    public bool Busy { get; set; }

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public void Clear()
    {
        Thread.Clear();
        Summarized.Clear();
        Summary = null;
        Context = null;
    }
}
