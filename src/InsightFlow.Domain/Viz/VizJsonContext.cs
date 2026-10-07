using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Viz;

/// <summary>
/// Source-generated System.Text.Json metadata for <see cref="VizSpec"/>. Using one context everywhere
/// (services, Web, agents, persistence) guarantees identical JSON — which the query cache key depends on.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(VizSpec))]
[JsonSerializable(typeof(FilterSpec))]
[JsonSerializable(typeof(FieldRef))]
public sealed partial class VizJsonContext : JsonSerializerContext;

/// <summary>Convenience (de)serialization of <see cref="VizSpec"/> through <see cref="VizJsonContext"/>.</summary>
public static class VizSpecJson
{
    public static string Serialize(VizSpec spec) =>
        JsonSerializer.Serialize(spec, VizJsonContext.Default.VizSpec);

    public static VizSpec Deserialize(string json) =>
        JsonSerializer.Deserialize(json, VizJsonContext.Default.VizSpec)
        ?? throw new JsonException("VizSpec JSON was null.");
}
