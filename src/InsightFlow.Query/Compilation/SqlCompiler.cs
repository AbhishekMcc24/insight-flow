using System.Globalization;
using System.Text;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Validation;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Dialects;

namespace InsightFlow.Query.Compilation;

/// <summary>
/// "VizQL-lite": compiles a validated <see cref="VizSpec"/> into one SQL statement.
/// <list type="bullet">
/// <item>Each encoding channel becomes an output column aliased by channel name (<c>x</c>, <c>y</c>, <c>color</c>…).</item>
/// <item>If any channel is aggregated, all non-aggregated channels form the GROUP BY.</item>
/// <item>Tables beyond the base table are joined along the model's relationships (shortest path, LEFT JOIN).</item>
/// <item>Identifiers come only from the semantic model and are quoted by the dialect; every literal is a parameter.</item>
/// <item>Relative dates are resolved to concrete parameter bounds using the injected clock.</item>
/// <item>Output is deterministic (stable ORDER BY, stable formatting) so results are cacheable and SQL is golden-testable.</item>
/// </list>
/// Calculated measures are inserted as their stored DuckDB expression; they are trusted model content validated by
/// the sandbox when saved, and should reference base-table columns (unqualified names may be ambiguous across joins).
/// </summary>
public sealed class SqlCompiler(TimeProvider clock) : ISqlCompiler
{
    public CompiledQuery Compile(VizSpec spec, SemanticModel model, IQueryDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(dialect);

        var validation = VizSpecValidator.Validate(spec, model);
        if (!validation.IsValid)
        {
            throw new VizSpecValidationException(validation.Errors);
        }

        return new Builder(spec, model, dialect, clock.GetUtcNow()).Build();
    }

    private sealed class Builder(VizSpec spec, SemanticModel model, IQueryDialect dialect, DateTimeOffset now)
    {
        private readonly List<QueryParameter> _parameters = [];
        private readonly Dictionary<string, string> _tableAliases = new(StringComparer.OrdinalIgnoreCase);

        public CompiledQuery Build()
        {
            var baseTable = model.Tables.First(t => t.SourceDatasetVersionId == spec.DatasetVersionId);
            var channels = Channels().ToList();

            var requiredTables = channels.Select(c => c.Field.Field)
                .Concat(spec.Filters.Select(f => f.Field))
                .Concat(spec.Filters.OfType<TopNFilter>().Select(t => t.By.Field))
                .Select(model.Resolve)
                .Where(r => r.Table is not null)
                .Select(r => r.Table!.Name);

            var joins = JoinPlanner.Plan(model, baseTable.Name, requiredTables)
                ?? throw new VizSpecValidationException(
                    [new ValidationError("no_join_path", "The fields come from tables that are not connected by relationships.", "encoding")]);

            var sources = new List<QuerySource> { new("ds0", baseTable.SourceDatasetVersionId) };
            _tableAliases[baseTable.Name] = "t0";
            foreach (var join in joins)
            {
                _tableAliases[join.Table] = $"t{_tableAliases.Count}";
                sources.Add(new QuerySource($"ds{sources.Count}", model.FindTable(join.Table)!.SourceDatasetVersionId));
            }

            var from = new StringBuilder();
            from.Append("FROM ").Append(dialect.QuoteIdentifier("ds0")).Append(" AS t0");
            for (var i = 0; i < joins.Count; i++)
            {
                var join = joins[i];
                var to = _tableAliases[join.Table];
                var fromAlias = _tableAliases[join.FromTable];
                from.Append('\n').Append("LEFT JOIN ").Append(dialect.QuoteIdentifier(sources[i + 1].RelationName)).Append(" AS ").Append(to)
                    .Append(" ON ")
                    .Append(string.Join(" AND ", join.Keys.Select(k =>
                        $"{fromAlias}.{dialect.QuoteIdentifier(k.FromColumn)} = {to}.{dialect.QuoteIdentifier(k.ToColumn)}")));
            }

            var selectItems = channels.Select(c => (c.Channel, c.Field, Expr: Expression(c.Field), Aggregated: IsAggregated(c.Field))).ToList();
            var anyAggregated = selectItems.Any(s => s.Aggregated);
            var groupBy = anyAggregated
                ? selectItems.Where(s => !s.Aggregated).Select(s => s.Expr).Distinct(StringComparer.Ordinal).ToList()
                : [];

            var plainFilters = spec.Filters.Where(f => f is not TopNFilter).Select(Condition).ToList();
            var conditions = plainFilters
                .Concat(spec.Filters.OfType<TopNFilter>().Select(t => TopNCondition(t, from.ToString(), plainFilters)))
                .ToList();

            var sql = new StringBuilder();
            sql.Append("SELECT\n")
                .Append(string.Join(",\n", selectItems.Select(s => $"  {s.Expr} AS {dialect.QuoteIdentifier(s.Channel)}")))
                .Append('\n').Append(from);

            if (conditions.Count > 0)
            {
                sql.Append("\nWHERE ").Append(string.Join("\n  AND ", conditions));
            }

            if (groupBy.Count > 0)
            {
                sql.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));
            }

            sql.Append("\nORDER BY ").Append(string.Join(", ", OrderBy(selectItems.Select(s => (s.Channel, s.Field, s.Aggregated)).ToList())));

            var limited = dialect.ApplyLimit(sql.ToString(), spec.Limit + 1);
            var columns = selectItems.Select(s => new OutputColumn(s.Channel, OutputType(s.Field), s.Channel, s.Field)).ToList();

            return new CompiledQuery(dialect.Name, limited, _parameters, columns, sources, spec.Limit);
        }

        private IEnumerable<(string Channel, FieldRef Field)> Channels()
        {
            var e = spec.Encoding;
            (string, FieldRef?)[] all = [("x", e.X), ("y", e.Y), ("color", e.Color), ("size", e.Size), ("facet", e.Facet), ("label", e.Label)];
            return all.Where(c => c.Item2 is not null).Select(c => (c.Item1, c.Item2!));
        }

        private bool IsAggregated(FieldRef field) => field.IsAggregated || model.Resolve(field.Field).IsMeasure;

        /// <summary>Unaggregated, untruncated column reference (used by filters).</summary>
        private string ColumnExpression(string field)
        {
            var r = model.Resolve(field);
            return r.IsMeasure
                ? $"({r.Measure!.Expression})"
                : $"{_tableAliases[r.Table!.Name]}.{dialect.QuoteIdentifier(r.Column!.Name)}";
        }

        private string Expression(FieldRef field)
        {
            var expr = ColumnExpression(field.Field);
            if (field.TimeUnit is { } unit)
            {
                expr = dialect.DateTrunc(unit, expr, dateOnly: model.Resolve(field.Field).DataType == DataType.Date);
            }

            return field.IsAggregated ? dialect.Aggregate(field.Agg, expr) : expr;
        }

        private DataType OutputType(FieldRef field)
        {
            var r = model.Resolve(field.Field);
            return field.Agg switch
            {
                Agg.Count or Agg.CountDistinct => DataType.Integer,
                Agg.Avg or Agg.Median => DataType.Decimal,
                _ => r.DataType,
            };
        }

        private List<string> OrderBy(List<(string Channel, FieldRef Field, bool Aggregated)> items)
        {
            var order = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var sort in spec.Sort ?? [])
            {
                var item = items.First(i => string.Equals(i.Field.Field, sort.Field, StringComparison.OrdinalIgnoreCase));
                if (used.Add(item.Channel))
                {
                    order.Add($"{dialect.QuoteIdentifier(item.Channel)} {(sort.Direction == SortDirection.Desc ? "DESC" : "ASC")}");
                }
            }

            // Deterministic tie-breakers: every grouping channel, then (for ungrouped queries) everything else.
            foreach (var item in items.Where(i => !i.Aggregated).Concat(items.Where(i => i.Aggregated)))
            {
                if (used.Add(item.Channel))
                {
                    order.Add($"{dialect.QuoteIdentifier(item.Channel)} ASC");
                }
            }

            return order;
        }

        private string Condition(FilterSpec filter)
        {
            var column = ColumnExpression(filter.Field);
            var type = model.Resolve(filter.Field).DataType;

            switch (filter)
            {
                case EqualsFilter eq when eq.Value.Kind == ScalarKind.Null:
                    return eq.Exclude ? $"{column} IS NOT NULL" : $"{column} IS NULL";

                case EqualsFilter eq:
                    var p = Parameter(eq.Value, type);
                    return eq.Exclude ? $"({column} <> {p} OR {column} IS NULL)" : $"{column} = {p}";

                case InFilter inFilter:
                    var hasNull = inFilter.Values.Any(v => v.Kind == ScalarKind.Null);
                    var values = inFilter.Values.Where(v => v.Kind != ScalarKind.Null).Select(v => Parameter(v, type)).ToList();
                    var list = string.Join(", ", values);
                    if (inFilter.Exclude)
                    {
                        if (hasNull)
                        {
                            return values.Count > 0 ? $"({column} IS NOT NULL AND {column} NOT IN ({list}))" : $"{column} IS NOT NULL";
                        }

                        return $"({column} NOT IN ({list}) OR {column} IS NULL)";
                    }

                    if (values.Count == 0)
                    {
                        return $"{column} IS NULL";
                    }

                    return hasNull ? $"({column} IN ({list}) OR {column} IS NULL)" : $"{column} IN ({list})";

                case RangeFilter range:
                    var parts = new List<string>();
                    if (range.Min is { } min)
                    {
                        parts.Add($"{column} {(range.MinInclusive ? ">=" : ">")} {Parameter(min, type)}");
                    }

                    if (range.Max is { } max)
                    {
                        parts.Add($"{column} {(range.MaxInclusive ? "<=" : "<")} {Parameter(max, type)}");
                    }

                    return parts.Count == 1 ? parts[0] : $"({string.Join(" AND ", parts)})";

                case RelativeDateFilter relative:
                    var (start, end) = RelativeDateRange.Compute(relative, now);
                    return $"({column} >= {Parameter(DateValue(start, type))} AND {column} < {Parameter(DateValue(end, type, isExclusiveEnd: true))})";

                default:
                    throw new NotSupportedException($"Filter {filter.GetType().Name} is not supported.");
            }
        }

        /// <summary><c>field IN (top N values of field ranked by By, under the other filters)</c>.</summary>
        private string TopNCondition(TopNFilter topN, string from, List<string> otherConditions)
        {
            var column = ColumnExpression(topN.Field);
            var rank = Expression(topN.By);
            var inner = new StringBuilder();
            inner.Append("SELECT ").Append(column).Append(' ').Append(from.Replace("\n", " ", StringComparison.Ordinal));
            if (otherConditions.Count > 0)
            {
                inner.Append(" WHERE ").Append(string.Join(" AND ", otherConditions));
            }

            inner.Append(" GROUP BY ").Append(column)
                .Append(" ORDER BY ").Append(rank).Append(topN.Direction == SortDirection.Desc ? " DESC" : " ASC")
                .Append(", ").Append(column).Append(" ASC");

            var limited = dialect.ApplyLimit(inner.ToString(), topN.N).Replace("\n", " ", StringComparison.Ordinal);
            return $"{column} IN ({limited})";
        }

        private string Parameter(ScalarValue value, DataType type) => Parameter(ToParameterValue(value, type));

        private string Parameter(object? value)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"p{_parameters.Count}");
            _parameters.Add(new QueryParameter(name, value));
            return dialect.Parameter(name);
        }

        private static object DateValue(DateTime utc, DataType type, bool isExclusiveEnd = false)
        {
            if (type != DataType.Date)
            {
                return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
            }

            // A date column compares against whole days; an exclusive end inside a day moves to the next day.
            var date = DateOnly.FromDateTime(utc);
            return isExclusiveEnd && utc.TimeOfDay > TimeSpan.Zero ? date.AddDays(1) : date;
        }

        /// <summary>Converts a JSON literal to the CLR value bound for a column of <paramref name="type"/> (types already validated).</summary>
        internal static object? ToParameterValue(ScalarValue value, DataType type) => value.Kind switch
        {
            ScalarKind.Null => null,
            ScalarKind.Boolean => value.Boolean,
            ScalarKind.Number when type == DataType.Integer && decimal.Truncate(value.Number) == value.Number
                                   && value.Number is >= long.MinValue and <= long.MaxValue => (long)value.Number,
            ScalarKind.Number => value.Number,
            ScalarKind.String when type == DataType.Date => DateOnly.FromDateTime(ParseUtc(value.Text!)),
            ScalarKind.String when type == DataType.DateTime => DateTime.SpecifyKind(ParseUtc(value.Text!), DateTimeKind.Unspecified),
            _ => value.Text,
        };

        private static DateTime ParseUtc(string text) =>
            DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;
    }
}
