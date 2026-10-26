using InsightFlow.Domain.Connections;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace InsightFlow.Connectors.Databases;

/// <summary>
/// The parts of a database connection string we accept: non-secret <see cref="Settings"/> in the same shape the connectors
/// already use (<c>server</c>/<c>host</c>, <c>database</c>, <c>user</c>…) and the <see cref="Password"/> (which belongs in the secret store).
/// </summary>
public sealed record ParsedConnection(IReadOnlyDictionary<string, string> Settings, string? Password);

/// <summary>
/// Turns a pasted connection string into the settings the connectors understand. Connection strings can carry options that
/// reach beyond a database login (file paths, attach-a-file, custom drivers, pooling…), so the string is parsed with the
/// driver's own parser and <i>only</i> host, port, database, user, password and TLS options are copied out; everything else is
/// dropped. The connectors then build their own, locked-down connection string from those values.
/// </summary>
public static class SourceConnectionStrings
{
    public static ParsedConnection Parse(DataSourceKind kind, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ConnectorException("Enter a connection string.");
        }

        try
        {
            return kind switch
            {
                DataSourceKind.SqlServer => ParseSqlServer(connectionString),
                DataSourceKind.PostgreSql => ParsePostgreSql(connectionString),
                DataSourceKind.MySql => ParseMySql(connectionString),
                _ => throw new ConnectorException($"Connection strings are not supported for {kind}."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new ConnectorException($"The connection string is not valid: {ex.Message}");
        }
    }

    private static ParsedConnection ParseSqlServer(string text)
    {
        var b = new SqlConnectionStringBuilder(text);
        if (b.IntegratedSecurity || b.Authentication is not (SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword or SqlAuthenticationMethod.ActiveDirectoryDefault))
        {
            throw new ConnectorException("Only SQL login (user and password) and Azure default credentials are supported. Remove Integrated Security or other authentication options.");
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Put(settings, "server", b.DataSource);
        Put(settings, "database", b.InitialCatalog);
        if (b.Authentication == SqlAuthenticationMethod.ActiveDirectoryDefault)
        {
            settings["authentication"] = "ActiveDirectoryDefault";
        }
        else
        {
            Put(settings, "user", b.UserID);
        }

        settings["encrypt"] = b.Encrypt == SqlConnectionEncryptOption.Optional ? "false" : "true";
        if (b.TrustServerCertificate)
        {
            settings["trustServerCertificate"] = "true";
        }

        return Require(settings, "server", new ParsedConnection(settings, NullIfEmpty(b.Password)));
    }

    private static ParsedConnection ParsePostgreSql(string text)
    {
        var b = new NpgsqlConnectionStringBuilder(text);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Put(settings, "host", b.Host);
        if (b.Port != 5432)
        {
            settings["port"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Put(settings, "database", b.Database);
        Put(settings, "user", b.Username);
        // A string that does not say anything about TLS gets the secure default, not the driver's "prefer".
        settings["sslmode"] = !Mentions(text, "SSL Mode", "SslMode") ? "require" : b.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Prefer => "prefer",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "require",
        };
        return Require(settings, "host", new ParsedConnection(settings, NullIfEmpty(b.Password)));
    }

    private static ParsedConnection ParseMySql(string text)
    {
        var b = new MySqlConnectionStringBuilder(text);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Put(settings, "host", b.Server);
        if (b.Port != 3306)
        {
            settings["port"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Put(settings, "database", b.Database);
        Put(settings, "user", b.UserID);
        settings["sslmode"] = !Mentions(text, "SSL Mode", "SslMode") ? "required" : b.SslMode switch
        {
            MySqlSslMode.None => "disable",
            MySqlSslMode.Preferred => "preferred",
            MySqlSslMode.VerifyCA => "verify-ca",
            MySqlSslMode.VerifyFull => "verify-full",
            _ => "required",
        };
        return Require(settings, "host", new ParsedConnection(settings, NullIfEmpty(b.Password)));
    }


    /// <summary>True when the pasted string itself sets one of the keywords (typed builders report every known key, so the raw text is checked).</summary>
    private static bool Mentions(string text, params string[] keywords)
    {
        var raw = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        return keywords.Any(raw.ContainsKey);
    }

    private static ParsedConnection Require(Dictionary<string, string> settings, string key, ParsedConnection result) =>
        settings.ContainsKey(key) ? result : throw new ConnectorException($"The connection string has no {(key == "server" ? "Server / Data Source" : "Host / Server")}.");

    private static void Put(Dictionary<string, string> settings, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            settings[key] = value.Trim();
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
