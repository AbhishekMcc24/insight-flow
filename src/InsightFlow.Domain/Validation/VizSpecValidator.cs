using System.Globalization;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Domain.Validation;

/// <summary>
/// Checks a <see cref="VizSpec"/> against a <see cref="SemanticModel"/> before it is compiled: every field
/// exists, aggregations and time units are legal for the column type, the mark has its required channels,
/// filters are well-formed and the row limit is within bounds. Applied to specs from the UI, saved workbooks
/// and — most importantly — the AI, whose output is never trusted.
/// </summary>
public static class VizSpecValidator
{
    public const int MaxInValues = 1_000;
    public const int MaxTopN = 1_000;
    public const int MaxRelativeDateCount = 1_000;

    public static ValidationResult Validate(VizSpec spec, SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);

        var result = new ValidationResult();

        if (spec.SchemaVersion != VizSpec.CurrentSchemaVersion)
        {
            result.Add("unsupported_schema_version", $"Schema version {spec.SchemaVersion} is not supported (current: {VizSpec.CurrentSchemaVersion}).", "schemaVersion");
        }

        if (!model.Tables.Any(t => t.SourceDatasetVersionId == spec.DatasetVersionId))
        {
            result.Add("dataset_not_in_model", "The dataset version is not part of the semantic model.", "datasetVersionId");
        }

        if (spec.Limit is < 1 or > VizSpec.MaxLimit)
        {
            result.Add("limit_out_of_range", $"Limit must be between 1 and {VizSpec.MaxLimit:N0}.", "limit");
        }

        ValidateChannels(spec, model, result);
        ValidateMarkRequirements(spec, result);
        ValidateSort(spec, result);

        for (var i = 0; i < spec.Filters.Count; i++)
        {
            ValidateFilter(spec.Filters[i], model, result, $"filters[{i}]");
        }

        return result;
    }

    private static void ValidateChannels(VizSpec spec, SemanticModel model, ValidationResult result)
    {
        var e = spec.Encoding;
        (string Name, FieldRef? Ref)[] channels =
        [
            ("x", e.X), ("y", e.Y), ("color", e.Color), ("size", e.Size), ("facet", e.Facet), ("label", e.Label),
        ];

        foreach (var (name, fieldRef) in channels)
        {
            if (fieldRef is not null)
            {
                ValidateFieldRef(fieldRef, model, result, $"encoding.{name}");
            }
        }
    }

    private static void ValidateFieldRef(FieldRef fieldRef, SemanticModel model, ValidationResult result, string path)
    {
        var resolution = model.Resolve(fieldRef.Field);
        if (!resolution.Success)
        {
            result.Add("unknown_field", resolution.Error!, path);
            return;
        }

        if (resolution.IsMeasure)
        {
            if (fieldRef.IsAggregated)
            {
                result.Add("measure_already_aggregated", $"'{fieldRef.Field}' is a calculated measure and cannot be aggregated again.", path);
            }

            if (fieldRef.TimeUnit is not null)
            {
                result.Add("time_unit_not_allowed", "A time unit cannot be applied to a calculated measure.", path);
            }

            return;
        }

        var type = resolution.DataType;
        if (!IsAggregationLegal(fieldRef.Agg, type))
        {
            result.Add("illegal_aggregation", $"{fieldRef.Agg} is not valid for '{fieldRef.Field}' ({type}).", path);
        }

        if (fieldRef.TimeUnit is { } unit)
        {
            if (!type.IsTemporal())
            {
                result.Add("time_unit_not_allowed", $"Time unit {unit} requires a date or date-time field; '{fieldRef.Field}' is {type}.", path);
            }
            else if (unit == TimeUnit.Hour && type != DataType.DateTime)
            {
                result.Add("time_unit_not_allowed", $"Time unit Hour requires a date-time field; '{fieldRef.Field}' is {type}.", path);
            }

            if (fieldRef.IsAggregated)
            {
                result.Add("time_unit_with_aggregation", "A field cannot have both a time unit and an aggregation.", path);
            }
        }
    }

    /// <summary>Which aggregations make sense for which column types.</summary>
    public static bool IsAggregationLegal(Agg agg, DataType type) => agg switch
    {
        Agg.None or Agg.Count or Agg.CountDistinct => true,
        Agg.Sum or Agg.Avg or Agg.Median => type.IsNumeric(),
        Agg.Min or Agg.Max => type.IsOrderable(),
        _ => false,
    };

    private static void ValidateMarkRequirements(VizSpec spec, ValidationResult result)
    {
        var e = spec.Encoding;
        switch (spec.Mark)
        {
            case Mark.Bar or Mark.Line or Mark.Area or Mark.Point:
                Require(e.X, "x", spec.Mark, result);
                Require(e.Y, "y", spec.Mark, result);
                break;
            case Mark.Heatmap:
                Require(e.X, "x", spec.Mark, result);
                Require(e.Y, "y", spec.Mark, result);
                Require(e.Color, "color", spec.Mark, result);
                break;
            case Mark.Pie:
                Require(e.Y, "y", spec.Mark, result);
                Require(e.Color, "color", spec.Mark, result);
                if (e.X is not null)
                {
                    result.Add("channel_not_allowed", "Pie charts use y (value) and color (category); x is not allowed.", "encoding.x");
                }

                break;
            case Mark.Table:
                if (!e.All().Any())
                {
                    result.Add("missing_channel", "A table needs at least one field.", "encoding");
                }

                break;
        }
    }

    private static void Require(FieldRef? channel, string name, Mark mark, ValidationResult result)
    {
        if (channel is null)
        {
            result.Add("missing_channel", $"{mark} charts require the '{name}' channel.", $"encoding.{name}");
        }
    }

    private static void ValidateSort(VizSpec spec, ValidationResult result)
    {
        if (spec.Sort is null)
        {
            return;
        }

        var encoded = spec.Encoding.All().Select(f => f.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < spec.Sort.Count; i++)
        {
            if (!encoded.Contains(spec.Sort[i].Field))
            {
                result.Add("sort_field_not_encoded", $"Sort field '{spec.Sort[i].Field}' must be one of the encoded fields.", $"sort[{i}]");
            }
        }
    }

    private static void ValidateFilter(FilterSpec filter, SemanticModel model, ValidationResult result, string path)
    {
        var resolution = model.Resolve(filter.Field);
        if (!resolution.Success)
        {
            result.Add("unknown_field", resolution.Error!, path);
            return;
        }

        if (resolution.IsMeasure && filter is not TopNFilter)
        {
            result.Add("filter_on_measure", "Filtering on calculated measures is not supported yet.", path);
            return;
        }

        var type = resolution.DataType;
        switch (filter)
        {
            case EqualsFilter eq:
                ValidateLiteral(eq.Value, type, result, $"{path}.value", allowNull: true);
                break;

            case InFilter inFilter:
                if (inFilter.Values.Count == 0 || inFilter.Values.Count > MaxInValues)
                {
                    result.Add("in_values_out_of_range", $"An IN filter needs between 1 and {MaxInValues:N0} values.", $"{path}.values");
                }

                for (var i = 0; i < inFilter.Values.Count; i++)
                {
                    ValidateLiteral(inFilter.Values[i], type, result, $"{path}.values[{i}]", allowNull: true);
                }

                break;

            case RangeFilter range:
                if (!type.IsNumeric() && !type.IsTemporal())
                {
                    result.Add("range_on_unordered_type", $"Range filters need a numeric or date field; '{filter.Field}' is {type}.", path);
                }

                if (range.Min is null && range.Max is null)
                {
                    result.Add("range_without_bounds", "A range filter needs a minimum, a maximum or both.", path);
                }

                if (range.Min is { } min)
                {
                    ValidateLiteral(min, type, result, $"{path}.min", allowNull: false);
                }

                if (range.Max is { } max)
                {
                    ValidateLiteral(max, type, result, $"{path}.max", allowNull: false);
                }

                break;

            case RelativeDateFilter relative:
                if (!type.IsTemporal())
                {
                    result.Add("relative_date_on_non_date", $"Relative date filters need a date field; '{filter.Field}' is {type}.", path);
                }

                if (relative.Count is < 1 or > MaxRelativeDateCount)
                {
                    result.Add("relative_date_count_out_of_range", $"Count must be between 1 and {MaxRelativeDateCount:N0}.", $"{path}.count");
                }

                break;

            case TopNFilter topN:
                if (topN.N is < 1 or > MaxTopN)
                {
                    result.Add("top_n_out_of_range", $"N must be between 1 and {MaxTopN:N0}.", $"{path}.n");
                }

                ValidateFieldRef(topN.By, model, result, $"{path}.by");
                if (!topN.By.IsAggregated && !model.Resolve(topN.By.Field).IsMeasure)
                {
                    result.Add("top_n_by_not_aggregated", "Top-N must rank by an aggregated field or a calculated measure.", $"{path}.by");
                }

                break;
        }
    }

    private static void ValidateLiteral(ScalarValue value, DataType type, ValidationResult result, string path, bool allowNull)
    {
        var ok = value.Kind switch
        {
            ScalarKind.Null => allowNull,
            ScalarKind.Number => type.IsNumeric(),
            ScalarKind.Boolean => type == DataType.Boolean,
            ScalarKind.String => type switch
            {
                DataType.Date or DataType.DateTime => DateTimeOffset.TryParse(value.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
                DataType.Integer or DataType.Decimal or DataType.Boolean => false,
                _ => true,
            },
            _ => false,
        };

        if (!ok)
        {
            result.Add("literal_type_mismatch", $"Value '{value}' is not a valid {type}.", path);
        }
    }
}
