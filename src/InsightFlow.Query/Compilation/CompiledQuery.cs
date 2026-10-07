using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Validation;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Compilation;

/// <summary>A bound parameter. Every filter literal becomes one; values are never spliced into SQL text.</summary>
public sealed record QueryParameter(string Name, object? Value);

/// <summary>A relation the SQL reads (<see cref="RelationName"/>) and the dataset version that backs it.</summary>
public sealed record QuerySource(string RelationName, Guid DatasetVersionId);

/// <summary>Metadata of one output column: its alias, the encoding channel and field reference it came from.</summary>
public sealed record OutputColumn(string Name, DataType DataType, string? Channel = null, FieldRef? Field = null);

/// <summary>
/// The result of compiling a <see cref="VizSpec"/>: SQL text for one dialect plus everything needed to execute it.
/// <see cref="Sql"/> requests <see cref="RowLimit"/> + 1 rows so the executor can report truncation.
/// </summary>
public sealed record CompiledQuery(
    string Dialect,
    string Sql,
    IReadOnlyList<QueryParameter> Parameters,
    IReadOnlyList<OutputColumn> Columns,
    IReadOnlyList<QuerySource> Sources,
    int RowLimit);

/// <summary>Compiles a chart specification against a semantic model into dialect-specific SQL.</summary>
public interface ISqlCompiler
{
    /// <exception cref="VizSpecValidationException">The spec is invalid for the model.</exception>
    CompiledQuery Compile(VizSpec spec, SemanticModel model, Dialects.IQueryDialect dialect);
}

/// <summary>Thrown when a spec fails validation or cannot be compiled (e.g. no join path). Carries every error.</summary>
public sealed class VizSpecValidationException : Exception
{
    public VizSpecValidationException(IReadOnlyList<ValidationError> errors)
        : base("The chart specification is invalid: " + string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))) =>
        Errors = errors;

    public VizSpecValidationException()
        : this([])
    {
    }

    public VizSpecValidationException(string message)
        : base(message) => Errors = [];

    public VizSpecValidationException(string message, Exception innerException)
        : base(message, innerException) => Errors = [];

    public IReadOnlyList<ValidationError> Errors { get; }
}
