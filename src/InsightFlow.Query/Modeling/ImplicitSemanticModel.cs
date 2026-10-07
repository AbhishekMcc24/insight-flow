using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Query.Modeling;

/// <summary>
/// A single-table semantic model derived from a dataset version's schema, so a freshly uploaded file (or an AI-derived
/// version) can be charted before anyone models it. Numeric columns default to measures, everything else to
/// dimensions; there are no relationships or calculated measures.
/// </summary>
public static class ImplicitSemanticModel
{
    public const string TableName = "data";

    public static SemanticModel For(DatasetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var columns = version.Schema.Columns
            .Select(c => new ModelColumn(c.Name, c.DataType, c.DataType.IsNumeric() ? ColumnRole.Measure : ColumnRole.Dimension))
            .ToList();

        // The model id is the version id: implicit models are 1:1 with versions and never stored.
        return new SemanticModel(version.Id, version.TenantId, $"Dataset {version.Id:N}", [new ModelTable(TableName, version.Id, columns)]);
    }
}
