using InsightFlow.Agents.Sandbox;

namespace InsightFlow.Agents.Tests;

/// <summary>The guard must reject every way out of "a single read-only SELECT over the input tables".</summary>
public sealed class SqlGuardTests
{
    private static readonly string[] Inputs = ["input", "input_2"];

    private static Task<SqlGuardResult> Check(string sql) => SqlGuard.CheckAsync(sql, Inputs, TestContext.Current.CancellationToken);

    public static readonly TheoryData<string, SqlRejection> Malicious = new()
    {
        // Multiple statements and statement smuggling
        { "SELECT 1; SELECT 2", SqlRejection.NotSingleSelect },
        { "SELECT * FROM input; DROP TABLE input", SqlRejection.NotSingleSelect },
        { "SELECT 1 /* ; */ ; ATTACH 'evil.db'", SqlRejection.NotSingleSelect },
        // Non-SELECT statements
        { "ATTACH 'evil.db' AS evil", SqlRejection.NotSingleSelect },
        { "COPY input TO 'C:/temp/exfil.csv'", SqlRejection.NotSingleSelect },
        { "COPY (SELECT * FROM input) TO '/tmp/x.parquet' (FORMAT PARQUET)", SqlRejection.NotSingleSelect },
        { "INSTALL httpfs", SqlRejection.NotSingleSelect },
        { "LOAD httpfs", SqlRejection.NotSingleSelect },
        { "PRAGMA database_list", SqlRejection.NotSingleSelect },
        { "SET enable_external_access = true", SqlRejection.NotSingleSelect },
        { "RESET lock_configuration", SqlRejection.NotSingleSelect },
        { "CREATE TABLE t AS SELECT 1", SqlRejection.NotSingleSelect },
        { "DELETE FROM input", SqlRejection.NotSingleSelect },
        { "UPDATE input SET revenue = 0", SqlRejection.NotSingleSelect },
        { "EXPORT DATABASE 'C:/temp/db'", SqlRejection.NotSingleSelect },
        { "CALL pragma_version()", SqlRejection.NotSingleSelect },
        // File readers and replacement scans
        { "SELECT * FROM read_csv('/etc/passwd')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM read_csv_auto('C:/Windows/win.ini')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM read_parquet('s3://bucket/x.parquet')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM read_text('/etc/shadow')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM glob('/home/*')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM '/etc/passwd'", SqlRejection.ForbiddenTable },
        { "SELECT * FROM 'https://example.com/data.csv'", SqlRejection.ForbiddenTable },
        { "SELECT * FROM input WHERE region IN (SELECT * FROM read_csv('/etc/hosts'))", SqlRejection.ForbiddenFunction },
        // Dynamic SQL and introspection
        { "SELECT * FROM query('ATTACH ''x.db''')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM query_table('input')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM duckdb_settings()", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM duckdb_extensions()", SqlRejection.ForbiddenFunction },
        { "SELECT current_setting('temp_directory')", SqlRejection.ForbiddenFunction },
        { "SELECT * FROM information_schema.tables", SqlRejection.ForbiddenTable },
        { "SELECT * FROM main.input", SqlRejection.ForbiddenTable },
        { "SELECT * FROM other_table", SqlRejection.ForbiddenTable },
        // Garbage
        { "", SqlRejection.Empty },
        { "   ", SqlRejection.Empty },
        { "SELEC * FRM input", SqlRejection.ParseError },
        { "SELECT * FROM input /* unterminated", SqlRejection.ParseError },
    };

    [Theory]
    [MemberData(nameof(Malicious))]
    public async Task Check_MaliciousSql_IsRejected(string sql, SqlRejection expected)
    {
        var result = await Check(sql);

        result.IsAllowed.ShouldBeFalse(sql);
        result.Rejection.ShouldBe(expected, sql);
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("SELECT * FROM input")]
    [InlineData("FROM input SELECT region, SUM(revenue) GROUP BY region")]
    [InlineData("SELECT region, SUM(revenue) AS r FROM input GROUP BY ALL ORDER BY r DESC LIMIT 5")]
    [InlineData("WITH t AS (SELECT * FROM input WHERE channel = 'Online') SELECT COUNT(*) FROM t")]
    [InlineData("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5) SELECT * FROM n")]
    [InlineData("SELECT a.region FROM input a JOIN input_2 b USING (order_id)")]
    [InlineData("SELECT * FROM range(10)")]
    [InlineData("SELECT unnest([1, 2, 3]) AS x")]
    [InlineData("SELECT *, revenue - cost AS margin, RANK() OVER (PARTITION BY region ORDER BY revenue DESC) FROM input")]
    [InlineData("SELECT 1 -- trailing comment")]
    [InlineData("/* leading comment */ SELECT COUNT(*) FROM input")]
    public async Task Check_ReadOnlySelect_IsAllowed(string sql)
    {
        var result = await Check(sql);

        result.IsAllowed.ShouldBeTrue(result.Message);
    }

    [Fact]
    public async Task Check_TooLong_IsRejected()
    {
        var result = await Check("SELECT " + new string('1', SqlGuard.MaxSqlLength));

        result.Rejection.ShouldBe(SqlRejection.TooLong);
    }
}
