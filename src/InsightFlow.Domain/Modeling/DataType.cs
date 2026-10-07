namespace InsightFlow.Domain.Modeling;

/// <summary>Logical column type, independent of any database. Dialects map these to physical types.</summary>
public enum DataType
{
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,

    /// <summary>JSON text (e.g. arrays from document stores, which are kept as JSON in v1).</summary>
    Json,
}

/// <summary>Type-category helpers used by validators, the compiler and the agents.</summary>
public static class DataTypeExtensions
{
    public static bool IsNumeric(this DataType type) => type is DataType.Integer or DataType.Decimal;

    public static bool IsTemporal(this DataType type) => type is DataType.Date or DataType.DateTime;

    /// <summary>Types with a meaningful order (MIN/MAX, ranges).</summary>
    public static bool IsOrderable(this DataType type) => type.IsNumeric() || type.IsTemporal() || type == DataType.String;
}
