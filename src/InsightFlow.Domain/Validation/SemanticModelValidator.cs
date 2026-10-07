using InsightFlow.Domain.Modeling;

namespace InsightFlow.Domain.Validation;

/// <summary>
/// Structural checks on a <see cref="SemanticModel"/> before it is saved: unique names, relationships that
/// point at real tables and columns, and measures that do not shadow columns. (Calculated measure SQL is
/// validated separately by the AI-SQL sandbox, which needs DuckDB.)
/// </summary>
public static class SemanticModelValidator
{
    public static ValidationResult Validate(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            result.Add("name_required", "The model needs a name.", "name");
        }

        if (model.Tables.Count == 0)
        {
            result.Add("no_tables", "The model needs at least one table.", "tables");
        }

        foreach (var duplicate in Duplicates(model.Tables.Select(t => t.Name)))
        {
            result.Add("duplicate_table", $"Table name '{duplicate}' is used more than once.", "tables");
        }

        for (var t = 0; t < model.Tables.Count; t++)
        {
            var table = model.Tables[t];
            if (string.IsNullOrWhiteSpace(table.Name))
            {
                result.Add("name_required", "Every table needs a name.", $"tables[{t}].name");
            }

            if (table.Columns.Any(c => string.IsNullOrWhiteSpace(c.Name)))
            {
                result.Add("name_required", "Every column needs a name.", $"tables[{t}].columns");
            }

            foreach (var duplicate in Duplicates(table.Columns.Select(c => c.Name)))
            {
                result.Add("duplicate_column", $"Column '{duplicate}' appears more than once in '{table.Name}'.", $"tables[{t}].columns");
            }
        }

        for (var r = 0; r < model.Relationships.Count; r++)
        {
            ValidateRelationship(model, model.Relationships[r], result, $"relationships[{r}]");
        }

        var columnNames = model.Tables.SelectMany(t => t.Columns).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var m = 0; m < model.Measures.Count; m++)
        {
            var measure = model.Measures[m];
            if (string.IsNullOrWhiteSpace(measure.Name) || string.IsNullOrWhiteSpace(measure.Expression))
            {
                result.Add("name_required", "Every measure needs a name and an expression.", $"measures[{m}]");
            }
            else if (columnNames.Contains(measure.Name))
            {
                result.Add("measure_shadows_column", $"Measure '{measure.Name}' has the same name as a column.", $"measures[{m}].name");
            }
        }

        foreach (var duplicate in Duplicates(model.Measures.Select(m => m.Name)))
        {
            result.Add("duplicate_measure", $"Measure name '{duplicate}' is used more than once.", "measures");
        }

        return result;
    }

    private static void ValidateRelationship(SemanticModel model, Relationship relationship, ValidationResult result, string path)
    {
        var from = model.FindTable(relationship.FromTable);
        var to = model.FindTable(relationship.ToTable);

        if (from is null)
        {
            result.Add("unknown_table", $"Relationship source table '{relationship.FromTable}' does not exist.", $"{path}.fromTable");
        }

        if (to is null)
        {
            result.Add("unknown_table", $"Relationship target table '{relationship.ToTable}' does not exist.", $"{path}.toTable");
        }

        if (relationship.Keys.Count == 0)
        {
            result.Add("no_join_keys", "A relationship needs at least one join key.", $"{path}.keys");
        }

        if (from is null || to is null)
        {
            return;
        }

        for (var k = 0; k < relationship.Keys.Count; k++)
        {
            var key = relationship.Keys[k];
            var fromColumn = from.FindColumn(key.FromColumn);
            var toColumn = to.FindColumn(key.ToColumn);
            if (fromColumn is null || toColumn is null)
            {
                result.Add("unknown_join_column", $"Join key {from.Name}.{key.FromColumn} = {to.Name}.{key.ToColumn} references a missing column.", $"{path}.keys[{k}]");
            }
            else if (fromColumn.DataType != toColumn.DataType)
            {
                result.Add("join_type_mismatch", $"Join key types differ: {fromColumn.DataType} vs {toColumn.DataType}.", $"{path}.keys[{k}]");
            }
        }
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
}
