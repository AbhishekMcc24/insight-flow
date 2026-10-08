using System.Globalization;
using System.Text;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Agents.Analyst;

/// <summary>
/// Everything an analyst run may touch, resolved and tenant-checked by the host before the agent starts: the tenant,
/// the dataset version the question is about and its semantic model. Tools can only see this — never other datasets.
/// </summary>
public sealed record AnalystContext(TenantId Tenant, DatasetVersion Version, SemanticModel Model)
{
    /// <summary>A compact description of the table the SQL runs against (columns, types, roles, synonyms).</summary>
    public string DescribeSchema()
    {
        var sb = new StringBuilder();
        sb.Append("Table `input` (").Append(Version.RowCount.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" rows). Columns:");
        var modelColumns = Model.Tables.Where(t => t.SourceDatasetVersionId == Version.Id).SelectMany(t => t.Columns)
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var column in Version.Schema.Columns)
        {
            sb.Append("- \"").Append(column.Name).Append("\" ").Append(column.DataType);
            if (modelColumns.TryGetValue(column.Name, out var meta))
            {
                sb.Append(", ").Append(meta.Role);
                if (meta.DisplayName is { } display)
                {
                    sb.Append(", label \"").Append(display).Append('"');
                }

                if (meta.Synonyms.Count > 0)
                {
                    sb.Append(", also called ").Append(string.Join(", ", meta.Synonyms.Select(s => $"\"{s}\"")));
                }

                if (meta.Description is { } description)
                {
                    sb.Append(" — ").Append(description);
                }
            }

            sb.AppendLine();
        }

        foreach (var measure in Model.Measures)
        {
            sb.Append("Measure \"").Append(measure.Name).Append("\" = ").Append(measure.Expression).AppendLine();
        }

        return sb.ToString();
    }
}
