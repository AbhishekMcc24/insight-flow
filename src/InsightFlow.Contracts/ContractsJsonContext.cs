using System.Text.Json;
using System.Text.Json.Serialization;
using InsightFlow.Contracts.Identity;
using InsightFlow.Contracts.Query;

namespace InsightFlow.Contracts;

/// <summary>
/// Source-generated System.Text.Json metadata for every DTO exchanged between Web, Api and the services.
/// Add each new contract type here; hosts register it with <c>ConfigureHttpJsonOptions</c> and typed clients use it.
/// The primitive registrations let <c>object?</c> cells in <see cref="QueryResult"/> serialize without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(CurrentUserResponse))]
[JsonSerializable(typeof(QueryResult))]
[JsonSerializable(typeof(VizQueryRequest))]
[JsonSerializable(typeof(VizQueryResponse))]
[JsonSerializable(typeof(PreviewRequest))]
[JsonSerializable(typeof(PreviewResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
