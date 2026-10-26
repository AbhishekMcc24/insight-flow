using InsightFlow.Contracts.Documents;

namespace InsightFlow.Web.Components.Workspace;

/// <summary>One question to the spreadsheets and its answer.</summary>
public sealed class AskExchange(string question)
{
    public string Question { get; } = question;

    public bool Working { get; set; } = true;

    public DocumentAnswerDto? Answer { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// The spreadsheet Q&amp;A chat, kept for the length of the person's session instead of inside the panel, so leaving the Workspace and coming
/// back finds the conversation as it was. A browser refresh starts a new session and so a new, empty chat.
/// </summary>
public sealed class SpreadsheetChatState
{
    public List<AskExchange> Thread { get; } = [];

    /// <summary>What has been typed in the question box but not sent.</summary>
    public string Draft { get; set; } = string.Empty;

    public bool Busy { get; set; }

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();
}
