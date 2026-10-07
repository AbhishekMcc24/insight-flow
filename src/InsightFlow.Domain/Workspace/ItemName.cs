using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Workspace;

/// <summary>
/// A validated name for a folder or item in the workspace tree. Rules are the intersection of what Windows,
/// macOS and Linux accept, so anything a user can name locally and drag in is either accepted as-is or
/// rejected with a clear reason — and a name can never be used for path traversal. Uniqueness inside a folder
/// is case-insensitive (see <see cref="Key"/>).
/// </summary>
[JsonConverter(typeof(ItemNameJsonConverter))]
public readonly record struct ItemName
{
    public const int MaxLength = 255;

    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("/\\:*?\"<>|");

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private ItemName(string value) => Value = value;

    public string Value { get; }

    /// <summary>Case-insensitive comparison key used for "unique within a folder" checks (mirrors the DB index on lower(name)).</summary>
    public string Key => Value.ToLowerInvariant();

    /// <summary>Lower-case extension including the dot (e.g. <c>.csv</c>), or empty. A leading dot (<c>.env</c>) is not an extension.</summary>
    public string Extension
    {
        get
        {
            var dot = Value.LastIndexOf('.');
            return dot > 0 ? Value[dot..].ToLowerInvariant() : string.Empty;
        }
    }

    /// <summary>The name without <see cref="Extension"/>.</summary>
    public string Stem => Extension.Length == 0 ? Value : Value[..^Extension.Length];

    public static ItemName Create(string? value) =>
        TryCreate(value, out var name, out var error) ? name : throw new DomainRuleException("invalid_name", error);

    public static bool TryCreate(string? value, out ItemName name, [NotNullWhen(false)] out string? error)
    {
        name = default;
        var trimmed = value?.Trim() ?? string.Empty;

        error = trimmed switch
        {
            { Length: 0 } => "A name cannot be empty.",
            { Length: > MaxLength } => $"A name cannot be longer than {MaxLength} characters.",
            "." or ".." => "'.' and '..' are not valid names.",
            _ when trimmed.AsSpan().ContainsAny(ForbiddenChars) => "A name cannot contain any of / \\ : * ? \" < > |.",
            _ when trimmed.Any(char.IsControl) => "A name cannot contain control characters.",
            _ when trimmed.EndsWith('.') => "A name cannot end with a dot.",
            _ when ReservedDeviceNames.Contains(DeviceStem(trimmed)) => $"'{trimmed}' is a reserved name.",
            _ => null,
        };

        if (error is not null)
        {
            return false;
        }

        name = new ItemName(trimmed.Normalize(System.Text.NormalizationForm.FormC));
        return true;
    }

    /// <summary>True when both names would collide in the same folder.</summary>
    public bool Collides(ItemName other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <summary>Returns <c>"stem (n).ext"</c>, used to resolve name conflicts on upload.</summary>
    public ItemName WithCounter(int n) =>
        Create(string.Create(CultureInfo.InvariantCulture, $"{TrimForCounter(Stem, n, Extension)} ({n}){Extension}"));

    public override string ToString() => Value;

    private static string DeviceStem(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? name[..dot] : name;
    }

    private static string TrimForCounter(string stem, int n, string extension)
    {
        var suffixLength = $" ({n})".Length + extension.Length;
        return stem.Length + suffixLength > MaxLength ? stem[..(MaxLength - suffixLength)] : stem;
    }
}

/// <summary>Serializes <see cref="ItemName"/> as a plain string (validated on read).</summary>
public sealed class ItemNameJsonConverter : JsonConverter<ItemName>
{
    public override ItemName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ItemName.TryCreate(reader.GetString(), out var name, out var error) ? name : throw new JsonException(error);

    public override void Write(Utf8JsonWriter writer, ItemName value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}
