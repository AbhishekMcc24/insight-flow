using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InsightFlow.Persistence;

/// <summary>Source-generated JSON for JSONB columns owned by Persistence (schemas, semantic model definitions).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DatasetSchema))]
[JsonSerializable(typeof(SemanticModelDefinition))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;

internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(v => v.Value, v => new TenantId(v));

internal sealed class ItemNameConverter() : ValueConverter<ItemName, string>(v => v.Value, v => ItemName.Create(v));

/// <summary>Stores a value as JSONB text through a source-generated <see cref="JsonTypeInfo{T}"/>.</summary>
internal sealed class JsonValueConverter<T>(JsonTypeInfo<T> typeInfo)
    : ValueConverter<T, string>(
        v => JsonSerializer.Serialize(v, typeInfo),
        v => JsonSerializer.Deserialize(v, typeInfo)!)
    where T : class;

/// <summary>Compares JSON-mapped values by their serialized form so change tracking detects edits inside them.</summary>
internal sealed class JsonValueComparer<T>(JsonTypeInfo<T> typeInfo)
    : ValueComparer<T>(
        (a, b) => JsonSerializer.Serialize<T>(a!, typeInfo) == JsonSerializer.Serialize<T>(b!, typeInfo),
        v => JsonSerializer.Serialize(v, typeInfo).GetHashCode(StringComparison.Ordinal),
        v => JsonSerializer.Deserialize(JsonSerializer.Serialize(v, typeInfo), typeInfo)!)
    where T : class;

internal sealed class GuidListConverter() : ValueConverter<IReadOnlyList<Guid>, Guid[]>(v => v.ToArray(), v => v);

internal sealed class GuidListComparer() : ValueComparer<IReadOnlyList<Guid>>(
    (a, b) => a!.SequenceEqual(b!),
    v => v.Aggregate(0, (h, g) => HashCode.Combine(h, g)),
    v => v.ToArray());
