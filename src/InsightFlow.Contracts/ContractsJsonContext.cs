using System.Text.Json;
using System.Text.Json.Serialization;
using InsightFlow.Contracts.Identity;

namespace InsightFlow.Contracts;

/// <summary>
/// Source-generated System.Text.Json metadata for every DTO exchanged between Web, Api and the services.
/// Add each new contract type here; hosts register it with <c>ConfigureHttpJsonOptions</c> and typed clients use it.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CurrentUserResponse))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
