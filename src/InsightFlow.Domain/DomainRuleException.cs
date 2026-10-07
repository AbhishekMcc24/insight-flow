namespace InsightFlow.Domain;

/// <summary>
/// Thrown when an operation would break a domain invariant (e.g. moving a folder into its own subtree).
/// Carries a stable machine-readable <see cref="Code"/> so APIs can map it to a ProblemDetails type
/// without parsing messages.
/// </summary>
public sealed class DomainRuleException : Exception
{
    public DomainRuleException(string code, string message)
        : base(message) => Code = code;

    public DomainRuleException()
        : this("domain_rule", "A domain rule was violated.")
    {
    }

    public DomainRuleException(string message)
        : this("domain_rule", message)
    {
    }

    public DomainRuleException(string message, Exception innerException)
        : base(message, innerException) => Code = "domain_rule";

    /// <summary>Stable, snake_case identifier of the violated rule.</summary>
    public string Code { get; }
}
