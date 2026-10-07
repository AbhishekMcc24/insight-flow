using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InsightFlow.Testing;

/// <summary>
/// Golden-file (snapshot) assertions. The expected output lives next to the test source in
/// <c>Golden/&lt;name&gt;.verified.&lt;ext&gt;</c> and is reviewed in pull requests like code.
/// <list type="bullet">
/// <item>On mismatch (or when the file is missing) the actual output is written to <c>&lt;name&gt;.received.&lt;ext&gt;</c>
/// (git-ignored) and the test fails with the first differing line.</item>
/// <item>To accept new output, review the <c>.received</c> file and rename it, or run the tests with
/// <c>INSIGHTFLOW_ACCEPT_GOLDEN=1</c> (never set in CI).</item>
/// </list>
/// Line endings are normalised to <c>\n</c> so snapshots are identical on Windows and Linux.
/// </summary>
public static class Golden
{
    public const string AcceptEnvironmentVariable = "INSIGHTFLOW_ACCEPT_GOLDEN";

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Asserts that <paramref name="actual"/> matches <c>Golden/{name}.verified.{extension}</c>.</summary>
    public static void Match(string actual, string name, string extension = "txt", [CallerFilePath] string callerFilePath = "")
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directory = Path.Combine(Path.GetDirectoryName(callerFilePath) ?? ".", "Golden");
        var verifiedPath = Path.Combine(directory, $"{name}.verified.{extension}");
        var receivedPath = Path.Combine(directory, $"{name}.received.{extension}");
        var normalized = Normalize(actual);

        if (File.Exists(verifiedPath) && Normalize(File.ReadAllText(verifiedPath)) == normalized)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(directory);
        if (string.Equals(Environment.GetEnvironmentVariable(AcceptEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(verifiedPath, normalized, new UTF8Encoding(false));
            File.Delete(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, normalized, new UTF8Encoding(false));

        throw new GoldenFileMismatchException(File.Exists(verifiedPath)
            ? $"Golden file mismatch for '{name}'. {FirstDifference(Normalize(File.ReadAllText(verifiedPath)), normalized)}\nReview {receivedPath}"
            : $"Golden file '{verifiedPath}' does not exist yet. Review {receivedPath} and rename it to .verified.{extension} (or set {AcceptEnvironmentVariable}=1).");
    }

    /// <summary>Re-indents <paramref name="json"/> deterministically and matches it against <c>Golden/{name}.verified.json</c>.</summary>
    public static void MatchJson(string json, string name, [CallerFilePath] string callerFilePath = "")
    {
        var node = JsonNode.Parse(json) ?? throw new ArgumentException("JSON was null.", nameof(json));
        Match(node.ToJsonString(IndentedJson), name, "json", callerFilePath);
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end of file>";
            var al = i < a.Length ? a[i] : "<end of file>";
            if (!string.Equals(el, al, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:\n  expected: {el}\n  actual:   {al}";
            }
        }

        return "Files differ only in trailing whitespace.";
    }
}

/// <summary>Thrown by <see cref="Golden"/> when output differs from the reviewed snapshot.</summary>
public sealed class GoldenFileMismatchException : Exception
{
    public GoldenFileMismatchException()
    {
    }

    public GoldenFileMismatchException(string message)
        : base(message)
    {
    }

    public GoldenFileMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
