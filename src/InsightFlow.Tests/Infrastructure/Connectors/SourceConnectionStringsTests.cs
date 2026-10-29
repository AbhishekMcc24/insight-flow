using InsightFlow.Domain.Connections;

namespace InsightFlow.Tests.Infrastructure.Connectors;

public sealed class SourceConnectionStringsTests
{
    [Fact]
    public void Parse_SqlServer_ReadsHostDatabaseUserAndPassword()
    {
        var parsed = SourceConnectionStrings.Parse(
            DataSourceKind.SqlServer,
            "Server=tcp:myserver.database.windows.net,1433;Initial Catalog=sales;User ID=reader;Password=s3cret;Encrypt=True;TrustServerCertificate=False");

        parsed.Settings["server"].ShouldBe("tcp:myserver.database.windows.net,1433");
        parsed.Settings["database"].ShouldBe("sales");
        parsed.Settings["user"].ShouldBe("reader");
        parsed.Settings["encrypt"].ShouldBe("true");
        parsed.Settings.ContainsKey("trustServerCertificate").ShouldBeFalse();
        parsed.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void Parse_SqlServer_PasswordIsNeverInTheSettings()
    {
        var parsed = SourceConnectionStrings.Parse(DataSourceKind.SqlServer, "Data Source=h;User ID=u;Password=topsecret");

        parsed.Settings.Values.ShouldNotContain("topsecret");
    }

    [Theory]
    [InlineData("Server=h;Integrated Security=True")]
    [InlineData("Server=h;Authentication=Active Directory Password;User ID=u;Password=p")]
    public void Parse_SqlServer_UnsupportedAuthentication_IsRejected(string text) =>
        Should.Throw<ConnectorException>(() => SourceConnectionStrings.Parse(DataSourceKind.SqlServer, text));

    [Fact]
    public void Parse_SqlServer_DangerousOptionsAreDropped()
    {
        var parsed = SourceConnectionStrings.Parse(
            DataSourceKind.SqlServer,
            "Server=h;Database=d;User ID=u;Password=p;AttachDbFilename=C:\\secret.mdf;Failover Partner=evil.example;Workstation ID=x;Packet Size=32768");

        parsed.Settings.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["database", "encrypt", "server", "user"]);
    }

    [Fact]
    public void Parse_PostgreSql_ReadsKeyValueFormat_AndOnlyAllowedOptions()
    {
        var parsed = SourceConnectionStrings.Parse(
            DataSourceKind.PostgreSql,
            "Host=db.example.com;Port=6543;Database=sales;Username=reader;Password=pw;SSL Mode=VerifyFull;Passfile=/etc/passwd;SSL Key=/root/key.pem");

        parsed.Settings["host"].ShouldBe("db.example.com");
        parsed.Settings["port"].ShouldBe("6543");
        parsed.Settings["database"].ShouldBe("sales");
        parsed.Settings["user"].ShouldBe("reader");
        parsed.Settings["sslmode"].ShouldBe("verify-full");
        parsed.Settings.Keys.ShouldNotContain("passfile");
        parsed.Password.ShouldBe("pw");
    }

    [Fact]
    public void Parse_PostgreSql_DefaultPortIsOmitted_AndSslDefaultsToRequire()
    {
        var parsed = SourceConnectionStrings.Parse(DataSourceKind.PostgreSql, "Host=h;Username=u;Password=p");

        parsed.Settings.ContainsKey("port").ShouldBeFalse();
        parsed.Settings["sslmode"].ShouldBe("require");
    }

    [Fact]
    public void Parse_MySql_ReadsServerPortDatabaseUser()
    {
        var parsed = SourceConnectionStrings.Parse(DataSourceKind.MySql, "Server=db;Port=3307;Database=shop;User ID=reader;Password=pw;SslMode=Preferred;AllowLoadLocalInfile=true");

        parsed.Settings["host"].ShouldBe("db");
        parsed.Settings["port"].ShouldBe("3307");
        parsed.Settings["database"].ShouldBe("shop");
        parsed.Settings["sslmode"].ShouldBe("preferred");
        parsed.Settings.Keys.ShouldNotContain("allowloadlocalinfile");
    }

    [Theory]
    [InlineData(DataSourceKind.SqlServer, "")]
    [InlineData(DataSourceKind.PostgreSql, "   ")]
    [InlineData(DataSourceKind.SqlServer, "this is not a connection string")]
    [InlineData(DataSourceKind.PostgreSql, "Username=u;Password=p")] // no host
    [InlineData(DataSourceKind.MySql, "Database=d;User ID=u")] // no server
    public void Parse_EmptyOrIncomplete_IsRejectedWithAReadableError(DataSourceKind kind, string text) =>
        Should.Throw<ConnectorException>(() => SourceConnectionStrings.Parse(kind, text)).Message.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Parse_UnsupportedKind_IsRejected() =>
        Should.Throw<ConnectorException>(() => SourceConnectionStrings.Parse(DataSourceKind.Oracle, "Data Source=x"));
}
