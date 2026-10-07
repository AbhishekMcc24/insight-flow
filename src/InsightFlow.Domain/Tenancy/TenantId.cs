using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Tenancy;

/// <summary>
/// Strongly-typed tenant identifier. Every tenant-owned row and blob path carries one (D1: multi-tenant SaaS),
/// and a distinct type makes it impossible to pass a dataset id where a tenant id is expected.
/// </summary>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(Guid Value) : IParsable<TenantId>
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");

    public static TenantId Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var id) ? id : throw new FormatException($"'{s}' is not a valid tenant id.");

    public static TenantId Parse(string s) => Parse(s, null);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TenantId result)
    {
        if (Guid.TryParse(s, out var guid) && guid != Guid.Empty)
        {
            result = new TenantId(guid);
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>Serializes <see cref="TenantId"/> as a plain GUID string.</summary>
public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}
