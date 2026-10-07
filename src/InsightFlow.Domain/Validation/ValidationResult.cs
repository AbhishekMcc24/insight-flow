namespace InsightFlow.Domain.Validation;

/// <summary>
/// One validation failure. <see cref="Code"/> is stable (snake_case) so callers, tests and the AI repair loop can
/// match on it; <see cref="Path"/> is a JSON-path-like location such as <c>encoding.y</c> or <c>filters[1]</c>.
/// </summary>
public sealed record ValidationError(string Code, string Message, string Path);

/// <summary>
/// Outcome of a domain validation. Validators collect every error rather than stopping at the first,
/// so the UI can highlight all problems and the agent can repair a spec in one round trip.
/// </summary>
public sealed class ValidationResult
{
    private readonly List<ValidationError> _errors = [];

    public IReadOnlyList<ValidationError> Errors => _errors;

    public bool IsValid => _errors.Count == 0;

    internal void Add(string code, string message, string path) => _errors.Add(new ValidationError(code, message, path));

    public override string ToString() =>
        IsValid ? "valid" : string.Join("; ", _errors.Select(e => $"{e.Path}: {e.Code} — {e.Message}"));
}
