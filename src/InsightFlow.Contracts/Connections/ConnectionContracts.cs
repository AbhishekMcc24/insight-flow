namespace InsightFlow.Contracts.Connections;

/// <summary>
/// Creates a saved connection. <see cref="Kind"/> is a source kind name (e.g. <c>SqlServer</c>); <see cref="Settings"/> holds
/// non-secret values; <see cref="Secret"/> (e.g. the password) goes straight to the secret store and is never returned.
/// </summary>
public sealed record CreateConnectionRequest(string Name, string Kind, IReadOnlyDictionary<string, string> Settings, string? Secret = null);

/// <summary>A saved connection, without its secret.</summary>
public sealed record ConnectionDto(Guid Id, string Name, string Kind, IReadOnlyDictionary<string, string> Settings, bool HasSecret, DateTimeOffset CreatedAt);

public sealed record ConnectionTestResponse(bool Success, string Message);

/// <summary>A table/view/collection that can be extracted; pass <see cref="Id"/> to <see cref="CreateExtractRequest.Table"/>.</summary>
public sealed record SourceTableDto(string Id, string Name, string? Schema, string Kind);

/// <summary>Queues an extract of <see cref="Table"/> into a dataset placed in <see cref="FolderId"/>.</summary>
public sealed record CreateExtractRequest(string Table, Guid FolderId, string? Name = null);
