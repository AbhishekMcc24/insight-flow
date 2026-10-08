using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Testing;

/// <summary>
/// The deterministic "Contoso Retail" semantic model used across tests, benchmarks and evals:
/// a <c>sales</c> fact table joined to <c>products</c> and <c>stores</c>, plus calculated measures.
/// Ids are fixed so golden files are stable.
/// </summary>
public static class RetailModel
{
    public static readonly TenantId Tenant = new(Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100"));
    public static readonly TenantId OtherTenant = new(Guid.Parse("1f1e1d1c-1b1a-4918-9716-151413121110"));

    public static readonly Guid ModelId = Guid.Parse("3a0b4c5d-0000-4000-8000-000000000001");
    public static readonly Guid SalesVersionId = Guid.Parse("5a1e5000-0000-4000-8000-000000000001");
    public static readonly Guid ProductsVersionId = Guid.Parse("5a1e5000-0000-4000-8000-000000000002");
    public static readonly Guid StoresVersionId = Guid.Parse("5a1e5000-0000-4000-8000-000000000003");

    /// <summary>Schema of the denormalized <c>retail_sales</c> table (RetailDataGenerator.WriteDenormalizedAsync and samples/retail_sales.csv).</summary>
    public static DatasetSchema DenormalizedSchema { get; } = new(
    [
        new SchemaColumn("order_id", DataType.Integer), new SchemaColumn("order_date", DataType.Date), new SchemaColumn("channel", DataType.String),
        new SchemaColumn("store_name", DataType.String), new SchemaColumn("region", DataType.String), new SchemaColumn("country", DataType.String),
        new SchemaColumn("product_name", DataType.String), new SchemaColumn("category", DataType.String), new SchemaColumn("subcategory", DataType.String),
        new SchemaColumn("quantity", DataType.Integer), new SchemaColumn("revenue", DataType.Decimal), new SchemaColumn("cost", DataType.Decimal),
        new SchemaColumn("is_returned", DataType.Boolean),
    ]);

    /// <summary>
    /// Curated single-table model over the denormalized sample (same content the Worker seeds for the dev tenant): business
    /// names, synonyms and calculated measures that ground the agents.
    /// </summary>
    public static SemanticModel CreateSample(TenantId tenant, Guid versionId) => new(
        Guid.NewGuid(),
        tenant,
        "Contoso Retail (sample)",
        [
            new ModelTable("retail_sales", versionId,
            [
                new("order_id", DataType.Integer, ColumnRole.Dimension, "Order ID"),
                new("order_date", DataType.Date, ColumnRole.Dimension, "Order date", Synonyms: ["date", "day", "when"]),
                new("channel", DataType.String, ColumnRole.Dimension, "Sales channel", "Online or Store", ["online or in-store"]),
                new("store_name", DataType.String, ColumnRole.Dimension, "Store"),
                new("region", DataType.String, ColumnRole.Dimension, "Region", Synonyms: ["area", "territory"]),
                new("country", DataType.String, ColumnRole.Dimension, "Country"),
                new("product_name", DataType.String, ColumnRole.Dimension, "Product"),
                new("category", DataType.String, ColumnRole.Dimension, "Category", Synonyms: ["product category"]),
                new("subcategory", DataType.String, ColumnRole.Dimension, "Subcategory"),
                new("quantity", DataType.Integer, ColumnRole.Measure, "Units sold", Synonyms: ["units", "volume"]),
                new("revenue", DataType.Decimal, ColumnRole.Measure, "Revenue", Synonyms: ["sales", "turnover"], Format: "C2"),
                new("cost", DataType.Decimal, ColumnRole.Measure, "Cost", Format: "C2"),
                new("is_returned", DataType.Boolean, ColumnRole.Dimension, "Returned"),
            ],
            "Retail sales", "One row per order line (sample data)."),
        ],
        [],
        [
            new CalculatedMeasure("profit", "SUM(revenue) - SUM(cost)", DataType.Decimal, "Profit", Format: "C2"),
            new CalculatedMeasure("margin_pct", "(SUM(revenue) - SUM(cost)) / NULLIF(SUM(revenue), 0)", DataType.Decimal, "Margin %", Format: "P1"),
        ]);

    public static SemanticModel Create() => new(
        ModelId,
        Tenant,
        "Contoso Retail",
        [
            new ModelTable("sales", SalesVersionId,
            [
                new("order_id", DataType.Integer, ColumnRole.Dimension, "Order ID"),
                new("order_date", DataType.Date, ColumnRole.Dimension, "Order date", Synonyms: ["date", "day"]),
                new("ordered_at", DataType.DateTime, ColumnRole.Dimension, "Ordered at"),
                new("store_id", DataType.Integer, ColumnRole.Dimension),
                new("product_id", DataType.Integer, ColumnRole.Dimension),
                new("channel", DataType.String, ColumnRole.Dimension, "Sales channel", Synonyms: ["online or in-store"]),
                new("quantity", DataType.Integer, ColumnRole.Measure, "Units sold", Synonyms: ["units"]),
                new("revenue", DataType.Decimal, ColumnRole.Measure, "Revenue", Synonyms: ["sales", "turnover"], Format: "C2"),
                new("cost", DataType.Decimal, ColumnRole.Measure, "Cost", Format: "C2"),
                new("is_returned", DataType.Boolean, ColumnRole.Dimension, "Returned"),
            ],
            "Sales", "One row per order line."),
            new ModelTable("products", ProductsVersionId,
            [
                new("product_id", DataType.Integer, ColumnRole.Dimension),
                new("product_name", DataType.String, ColumnRole.Dimension, "Product"),
                new("category", DataType.String, ColumnRole.Dimension, "Category"),
                new("subcategory", DataType.String, ColumnRole.Dimension, "Subcategory"),
            ],
            "Products"),
            new ModelTable("stores", StoresVersionId,
            [
                new("store_id", DataType.Integer, ColumnRole.Dimension),
                new("store_name", DataType.String, ColumnRole.Dimension, "Store"),
                new("region", DataType.String, ColumnRole.Dimension, "Region", Synonyms: ["area"]),
                new("country", DataType.String, ColumnRole.Dimension, "Country"),
            ],
            "Stores"),
        ],
        [
            new Relationship("sales", "products", [new JoinKey("product_id", "product_id")]),
            new Relationship("sales", "stores", [new JoinKey("store_id", "store_id")]),
        ],
        [
            new CalculatedMeasure("profit", "SUM(revenue) - SUM(cost)", DataType.Decimal, "Profit", Format: "C2"),
            new CalculatedMeasure("margin_pct", "(SUM(revenue) - SUM(cost)) / NULLIF(SUM(revenue), 0)", DataType.Decimal, "Margin %", Format: "P1"),
        ]);
}
