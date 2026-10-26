using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InsightFlow.Contracts.Admin;
using InsightFlow.Contracts.Agents;
using InsightFlow.Contracts.Connections;
using InsightFlow.Contracts.Knowledge;
using InsightFlow.Contracts.Query;
using InsightFlow.Contracts.Workspace;

namespace InsightFlow.Web.Services;

/// <summary>Typed client for the Api's <c>/api/v1/workspace</c> endpoints (service discovery name <c>api</c>).</summary>
public sealed class WorkspaceApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/workspace";

    public Task<WorkspaceRootsResponse> GetRootsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/roots", Json.WorkspaceRootsResponse, ct);

    public Task<WorkspaceStatsDto> GetStatsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/stats", Json.WorkspaceStatsDto, ct);

    public Task<FolderContentsResponse> GetFolderAsync(Guid folderId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/folders/{folderId}/children", Json.FolderContentsResponse, ct);

    public Task<IReadOnlyList<DatasetSummaryDto>> ListDatasetsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/datasets", Json.IReadOnlyListDatasetSummaryDto, ct);

    public Task<FolderDto> CreateFolderAsync(Guid parentId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/folders", JsonBody(new CreateFolderRequest(parentId, name), Json.CreateFolderRequest), Json.FolderDto, ct);

    public Task<FolderDto> RenameFolderAsync(Guid folderId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{Base}/folders/{folderId}", JsonBody(new RenameRequest(name), Json.RenameRequest), Json.FolderDto, ct);

    public Task<ContentItemDto> RenameItemAsync(Guid itemId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{Base}/items/{itemId}", JsonBody(new RenameRequest(name), Json.RenameRequest), Json.ContentItemDto, ct);

    public Task MoveAsync(MoveRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/move", JsonBody(request, Json.MoveRequest), ct);

    public Task DeleteFolderAsync(Guid folderId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/folders/{folderId}", content: null, ct);

    public Task DeleteItemAsync(Guid itemId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/items/{itemId}", content: null, ct);

    public Task<ExtractQueuedResponse> CreateDatasetAsync(Guid itemId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/items/{itemId}/dataset", content: null, Json.ExtractQueuedResponse, ct);

    public Task<ExtractRunDto> GetExtractRunAsync(Guid runId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"/api/v1/extract-runs/{runId}", Json.ExtractRunDto, ct);

    /// <summary>Opens a file download (headers read, body streamed). Caller disposes the response.</summary>
    public Task<HttpResponseMessage> OpenDownloadAsync(Guid itemId, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Get, $"{Base}/items/{itemId}/content", content: null, HttpCompletionOption.ResponseHeadersRead, ct);
}

/// <summary>
/// Streams a multipart upload body to the Api without buffering. Uses its own HttpClient without the resilience
/// handler: a streamed request body cannot be replayed, so retries/hedging must not apply.
/// </summary>
public sealed class WorkspaceUploadClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    public async Task<UploadResponse> ForwardAsync(Guid folderId, Stream body, string contentType, long? contentLength, CancellationToken ct)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Headers.ContentLength = contentLength;
        return await SendAsync(HttpMethod.Post, $"/api/v1/workspace/folders/{folderId}/files", content, Json.UploadResponse, ct);
    }
}

/// <summary>Typed client for QueryService (<c>queryservice</c>).</summary>
public sealed class QueryApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    public Task<VizQueryResponse> VizAsync(VizQueryRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/viz", JsonBody(request, Json.VizQueryRequest), Json.VizQueryResponse, ct);

    public Task<PreviewResponse> PreviewAsync(Guid datasetVersionId, int limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/preview", JsonBody(new PreviewRequest(datasetVersionId, limit), Json.PreviewRequest), Json.PreviewResponse, ct);
}

/// <summary>Typed client for AgentService (<c>agentservice</c>); model calls are slow, so its timeouts are longer.</summary>
public sealed class AgentApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    /// <summary>Asks the analyst a question and yields its server-sent events as they arrive.</summary>
    public async IAsyncEnumerable<AgentEvent> AskAnalystAsync(AnalystRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, "/api/v1/agent/analyst", JsonBody(request, Json.AnalystRequest), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var parser = SseParser.Create(stream, (_, data) => JsonSerializer.Deserialize(data, Json.AgentEvent));
        await foreach (var item in parser.EnumerateAsync(ct))
        {
            if (item.Data is { } agentEvent)
            {
                yield return agentEvent;
            }
        }
    }

    /// <summary>Step 2 of a question: one DuckDB SELECT over the chosen tables whose joins are graph relationships. Nothing is run.</summary>
    /// <summary>Runs a plan's SQL over the copies of its tables in the locked sandbox.</summary>
    /// <summary>Proposes description changes that make a wrongly answered question come out right next time. Nothing is stored.</summary>
    public Task<KnowledgeChatResponse> FixQueryAsync(QueryFixRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/knowledge/query/fix", JsonBody(request, Json.QueryFixRequest), Json.KnowledgeChatResponse, ct);

    /// <summary>Asks a question about the prepared spreadsheets (DuckDB SQL over their tables, run in the sandbox).</summary>
    public Task<InsightFlow.Contracts.Documents.DocumentAnswerDto> AskDocumentsAsync(InsightFlow.Contracts.Documents.DocumentAskRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/documents/ask", JsonBody(request, Json.DocumentAskRequest), Json.DocumentAnswerDto, ct);

    public Task<QueryRunDto> RunQueryAsync(QueryRunRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/knowledge/query/run", JsonBody(request, Json.QueryRunRequest), Json.QueryRunDto, ct);

    /// <summary>The very first step of a question: the guardrails check what was typed. Refused text throws a <see cref="ServiceCallException"/> with a message safe to show.</summary>
    public Task CheckTextAsync(string text, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/guard/check", JsonBody(new GuardCheckRequest(text), Json.GuardCheckRequest), Json.GuardVerdictDto, ct);

    /// <summary>Measures a conversation against the model's context window; at the configured share it is summarized (see <see cref="ConversationCompactDto"/>).</summary>
    public Task<ConversationCompactDto> CompactConversationAsync(IReadOnlyList<QueryTurnDto> turns, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/conversation/compact", JsonBody(new ConversationCompactRequest(turns), Json.ConversationCompactRequest), Json.ConversationCompactDto, ct);

    /// <summary>Step 2 of a question: the agent chooses the tables it needs.</summary>
    public Task<TablePickResponse> PickTablesAsync(TablePickRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/tables/pick", JsonBody(request, Json.TablePickRequest), Json.TablePickResponse, ct);

    /// <summary>Forecasts the tokens and cost of a large AI request (descriptions, relationships or skills) before it is made.</summary>
    public Task<InsightFlow.Contracts.Agents.AiEstimateDto> EstimateAsync(InsightFlow.Contracts.Agents.AiEstimateRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/estimate", JsonBody(request, Json.AiEstimateRequest), Json.AiEstimateDto, ct);

    /// <summary>Step 1 of a question: the agent chooses the skills it needs and the tables they involve.</summary>
    public Task<InsightFlow.Contracts.Skills.SkillPickResponse> PickSkillsAsync(InsightFlow.Contracts.Skills.SkillPickRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/skills/pick", JsonBody(request, Json.SkillPickRequest), Json.SkillPickResponse, ct);

    /// <summary>AI drafts or improves skills from the knowledge base; nothing is stored.</summary>
    public Task<InsightFlow.Contracts.Skills.SkillAssistResponse> AssistSkillsAsync(InsightFlow.Contracts.Skills.SkillAssistRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/skills/assist", JsonBody(request, Json.SkillAssistRequest), Json.SkillAssistResponse, ct);

    public Task<QueryPlanDto> PlanQueryAsync(QueryPlanRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/knowledge/query/plan", JsonBody(request, Json.QueryPlanRequest), Json.QueryPlanDto, ct);

    public Task<KnowledgeSuggestionsDto> SuggestKnowledgeAsync(bool onlyMissing, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/knowledge/suggest", JsonBody(new KnowledgeSuggestRequest(onlyMissing), Json.KnowledgeSuggestRequest), Json.KnowledgeSuggestionsDto, ct);

    public Task<KnowledgeChatResponse> ChatKnowledgeAsync(KnowledgeChatRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/knowledge/chat", JsonBody(request, Json.KnowledgeChatRequest), Json.KnowledgeChatResponse, ct);

    public Task<DerivedFieldResponse> DerivedFieldAsync(DerivedFieldRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/derived-field", JsonBody(request, Json.DerivedFieldRequest), Json.DerivedFieldResponse, ct);
}

/// <summary>Typed client for the Api's administration endpoints (<c>/api/v1/admin</c>) and the caller's navigation menu.</summary>
public sealed class AdminApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/admin";

    public Task<IReadOnlyList<MenuItemDto>> GetMyNavigationAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "/api/v1/navigation", Json.IReadOnlyListMenuItemDto, ct);

    public Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/users", Json.IReadOnlyListUserDto, ct);

    public Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/users", JsonBody(request, Json.CreateUserRequest), Json.UserDto, ct);

    public Task<UserDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/users/{userId}", JsonBody(request, Json.UpdateUserRequest), Json.UserDto, ct);

    public Task SetPasswordAsync(Guid userId, string password, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/users/{userId}/password", JsonBody(new SetPasswordRequest(password), Json.SetPasswordRequest), ct);

    public Task<UserMenuAccessDto> GetUserAccessAsync(Guid userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/users/{userId}/menus", Json.UserMenuAccessDto, ct);

    public Task<UserMenuAccessDto> SetUserAccessAsync(Guid userId, UserMenuAccessDto access, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/users/{userId}/menus", JsonBody(access, Json.UserMenuAccessDto), Json.UserMenuAccessDto, ct);

    public Task<IReadOnlyList<MenuItemDto>> ListMenusAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/menus", Json.IReadOnlyListMenuItemDto, ct);

    public Task<MenuItemDto> CreateMenuAsync(SaveMenuItemRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/menus", JsonBody(request, Json.SaveMenuItemRequest), Json.MenuItemDto, ct);

    public Task<MenuItemDto> UpdateMenuAsync(Guid menuId, SaveMenuItemRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/menus/{menuId}", JsonBody(request, Json.SaveMenuItemRequest), Json.MenuItemDto, ct);

    public Task DeleteMenuAsync(Guid menuId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/menus/{menuId}", content: null, ct);
}

/// <summary>Typed client for the Api's Knowledge Base endpoints (<c>/api/v1/knowledge</c>).</summary>
public sealed class KnowledgeApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/knowledge";

    public Task<KnowledgeBaseConfigDto> GetConfigAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/config", Json.KnowledgeBaseConfigDto, ct);

    public Task<KnowledgeBaseConfigDto> SaveConfigAsync(SaveKnowledgeBaseConfigRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/config", JsonBody(request, Json.SaveKnowledgeBaseConfigRequest), Json.KnowledgeBaseConfigDto, ct);

    public Task<KnowledgeTestResultDto> TestAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/config/test", content: null, Json.KnowledgeTestResultDto, ct);

    public Task<KnowledgeGraphSummaryDto> GetSummaryAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/graph/summary", Json.KnowledgeGraphSummaryDto, ct);

    public Task<KnowledgeSeedResultDto> PushAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/graph/push", content: null, Json.KnowledgeSeedResultDto, ct);

    public Task<KnowledgeDefinitionsDto> GetDefinitionsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/definitions", Json.KnowledgeDefinitionsDto, ct);

    public Task<ImportResultDto> ImportTablesAsync(ImportTablesRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/import", JsonBody(request, Json.ImportTablesRequest), Json.ImportResultDto, ct);

    public Task<SourceTestResultDto> TestSourceAsync(SourceConnectionRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/source/test", JsonBody(request, Json.SourceConnectionRequest), Json.SourceTestResultDto, ct);

    public Task<ConnectionDto> SaveSourceAsync(SourceConnectionRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/source/connections", JsonBody(request, Json.SourceConnectionRequest), Json.ConnectionDto, ct);

    public Task DeleteSourceAsync(Guid connectionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/source/connections/{connectionId}", content: null, ct);

    public Task<IReadOnlyList<string>> SourceDatabasesAsync(Guid connectionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/source/connections/{connectionId}/databases", Json.IReadOnlyListString, ct);

    public Task<IReadOnlyList<SourceTableDto>> SourceTablesAsync(Guid connectionId, string? database, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/source/connections/{connectionId}/tables" + (string.IsNullOrEmpty(database) ? "" : $"?database={Uri.EscapeDataString(database)}"), Json.IReadOnlyListSourceTableDto, ct);

    public Task<SourceSchemaDto> DescribeSourceAsync(Guid connectionId, SourceDescribeRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/source/connections/{connectionId}/describe", JsonBody(request, Json.SourceDescribeRequest), Json.SourceSchemaDto, ct);

    public Task<ImportResultDto> ImportDatasetsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/import-datasets", content: null, Json.ImportResultDto, ct);

    public Task<KnowledgeTableDto> CreateTableAsync(SaveKnowledgeTableRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/tables", JsonBody(request, Json.SaveKnowledgeTableRequest), Json.KnowledgeTableDto, ct);

    public Task<KnowledgeTableDto> UpdateTableAsync(Guid tableId, SaveKnowledgeTableRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/definitions/tables/{tableId}", JsonBody(request, Json.SaveKnowledgeTableRequest), Json.KnowledgeTableDto, ct);

    /// <summary>Step 1 of a question: the tables and joins the knowledge graph picks for it (read through Neo4j).</summary>
    /// <summary>Makes sure a table has a recent copy of its rows to answer from (first time can take a while).</summary>
    public Task<QueryExtractDto> PrepareQueryTableAsync(Guid tableId, bool refresh, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/v1/query/tables/{tableId}/extract", JsonBody(new QueryExtractRequest(refresh), Json.QueryExtractRequest), Json.QueryExtractDto, ct);

    /// <summary>Keeps the person's verdict on an answer (right, or wrong with the reason).</summary>
    public Task RecordQueryFeedbackAsync(QueryFeedbackRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/feedback", JsonBody(request, Json.QueryFeedbackRequest), ct);

    /// <summary>The signed-in user's roles (to know whether they may change descriptions).</summary>
    public Task<InsightFlow.Contracts.Identity.CurrentUserResponse> GetMeAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "/api/v1/me", Json.CurrentUserResponse, ct);

    public Task<QueryContextDto> GetQueryContextAsync(string question, CancellationToken ct, IReadOnlyList<Guid>? extraTableIds = null, bool chosenByAgent = false) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/context", JsonBody(new QueryContextRequest(question, extraTableIds, chosenByAgent), Json.QueryContextRequest), Json.QueryContextDto, ct);

    /// <summary>What is known about the stored procedures that use the knowledge base's tables, and when they were last read.</summary>
    public Task<KnowledgeProceduresDto> GetProceduresAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/procedures", Json.KnowledgeProceduresDto, ct);

    /// <summary>Reads the stored procedures of the source databases and keeps those that use knowledge-base tables (source text only, never rows).</summary>
    public Task<KnowledgeProceduresDto> ScanProceduresAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/procedures/scan", content: null, Json.KnowledgeProceduresDto, ct);

    public Task<KnowledgeDefinitionsDto> SaveDatabaseDescriptionAsync(SaveDatabaseDescriptionRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/definitions/databases", JsonBody(request, Json.SaveDatabaseDescriptionRequest), Json.KnowledgeDefinitionsDto, ct);

    public Task<RemoveTablesResultDto> RemoveTablesAsync(IReadOnlyList<Guid> tableIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/tables/remove", JsonBody(new RemoveTablesRequest(tableIds), Json.RemoveTablesRequest), Json.RemoveTablesResultDto, ct);

    public Task DeleteTableAsync(Guid tableId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/definitions/tables/{tableId}", content: null, ct);

    public Task RemoveColumnAsync(Guid columnId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/definitions/columns/{columnId}", content: null, ct);

    public Task<KnowledgeDefinitionsDto> AddCompositeRelationshipAsync(SaveCompositeRelationshipRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/relationships/composite", JsonBody(request, Json.SaveCompositeRelationshipRequest), Json.KnowledgeDefinitionsDto, ct);

    public Task<KnowledgeDefinitionsDto> AddRelationshipAsync(SaveKnowledgeRelationshipRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/relationships", JsonBody(request, Json.SaveKnowledgeRelationshipRequest), Json.KnowledgeDefinitionsDto, ct);

    public Task DeleteRelationshipAsync(Guid relationshipId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/definitions/relationships/{relationshipId}", content: null, ct);

    public Task<KnowledgeDefinitionsDto> ApplySuggestionsAsync(ApplySuggestionsRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/definitions/suggestions/apply", JsonBody(request, Json.ApplySuggestionsRequest), Json.KnowledgeDefinitionsDto, ct);
}

/// <summary>Typed client for saved database connections (<c>/api/v1/connections</c>): list, add, test and discover tables.</summary>
public sealed class ConnectionsApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/connections";

    public Task<IReadOnlyList<ConnectionDto>> ListAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Base + "/", Json.IReadOnlyListConnectionDto, ct);

    public Task<ConnectionDto> CreateAsync(CreateConnectionRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Base + "/", JsonBody(request, Json.CreateConnectionRequest), Json.ConnectionDto, ct);

    public Task<ConnectionTestResponse> TestAsync(Guid connectionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/{connectionId}/test", content: null, Json.ConnectionTestResponse, ct);

    public Task<IReadOnlyList<SourceTableDto>> TablesAsync(Guid connectionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/{connectionId}/tables", Json.IReadOnlyListSourceTableDto, ct);
}

/// <summary>Spreadsheets for questions: choose Excel files, prepare them (DuckDB parses every sheet), answer problems, follow progress.</summary>
public sealed class DocumentsApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/documents";

    public Task<IReadOnlyList<InsightFlow.Contracts.Documents.SpreadsheetFileDto>> ListFilesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/files", Json.IReadOnlyListSpreadsheetFileDto, ct);

    public Task<InsightFlow.Contracts.Documents.DocumentSummaryDto> GetSummaryAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/summary", Json.DocumentSummaryDto, ct);

    /// <summary>The most recent preparation run, or null when there has been none.</summary>
    public async Task<InsightFlow.Contracts.Documents.IngestRunDto?> GetLatestRunAsync(CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"{Base}/runs/latest", content: null, HttpCompletionOption.ResponseContentRead, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync(Json.IngestRunDto, ct);
    }

    public Task<InsightFlow.Contracts.Documents.IngestRunDto> GetRunAsync(Guid runId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/runs/{runId}", Json.IngestRunDto, ct);

    public Task<InsightFlow.Contracts.Documents.IngestRunDto> StartRunAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/runs", JsonBody(new InsightFlow.Contracts.Documents.StartIngestRequest(itemIds), Json.StartIngestRequest), Json.IngestRunDto, ct);

    public Task<InsightFlow.Contracts.Documents.IngestRunDto> ResolveAsync(Guid runId, Guid problemId, string option, string? text, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Post,
            $"{Base}/runs/{runId}/problems/{problemId}/resolve",
            JsonBody(new InsightFlow.Contracts.Documents.ResolveProblemRequest(option, text), Json.ResolveProblemRequest),
            Json.IngestRunDto,
            ct);

    public Task RemoveFromIndexAsync(Guid itemId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/files/{itemId}/index", content: null, ct);
}

/// <summary>Organization-level skills: categories, skills, their versions and on/off state (Sys-admin only).</summary>
public sealed class SkillsApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/skills";

    public Task<IReadOnlyList<InsightFlow.Contracts.Skills.SkillCategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/categories", Json.IReadOnlyListSkillCategoryDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillCategoryDto> CreateCategoryAsync(InsightFlow.Contracts.Skills.SaveSkillCategoryRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/categories", JsonBody(request, Json.SaveSkillCategoryRequest), Json.SkillCategoryDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillCategoryDto> UpdateCategoryAsync(Guid id, InsightFlow.Contracts.Skills.SaveSkillCategoryRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/categories/{id}", JsonBody(request, Json.SaveSkillCategoryRequest), Json.SkillCategoryDto, ct);

    public Task DeleteCategoryAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/categories/{id}", content: null, ct);

    public Task<IReadOnlyList<InsightFlow.Contracts.Skills.SkillSummaryDto>> ListSkillsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Base, Json.IReadOnlyListSkillSummaryDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> GetSkillAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/{id}", Json.SkillDetailDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> CreateSkillAsync(InsightFlow.Contracts.Skills.SaveSkillRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Base, JsonBody(request, Json.SaveSkillRequest), Json.SkillDetailDto, ct);

    /// <summary>Saves an edit as a new version, active at once.</summary>
    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> SaveSkillAsync(Guid id, InsightFlow.Contracts.Skills.SaveSkillRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/{id}", JsonBody(request, Json.SaveSkillRequest), Json.SkillDetailDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/{id}/enabled", JsonBody(new InsightFlow.Contracts.Skills.SetSkillEnabledRequest(enabled), Json.SetSkillEnabledRequest), Json.SkillDetailDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> ActivateVersionAsync(Guid id, Guid versionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/{id}/versions/{versionId}/activate", content: null, Json.SkillDetailDto, ct);

    public Task<InsightFlow.Contracts.Skills.SkillDetailDto> DuplicateAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/{id}/duplicate", content: null, Json.SkillDetailDto, ct);

    public Task<InsightFlow.Contracts.Skills.ApplySkillSuggestionsResponse> ApplySuggestionsAsync(InsightFlow.Contracts.Skills.ApplySkillSuggestionsRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/suggestions/apply", JsonBody(request, Json.ApplySkillSuggestionsRequest), Json.ApplySkillSuggestionsResponse, ct);

    public Task DeleteSkillAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/{id}", content: null, ct);
}

/// <summary>The caller's Playground sheets: free-form canvases of text, shapes, charts and tables.</summary>
public sealed class PlaygroundApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/playground";

    public Task<IReadOnlyList<InsightFlow.Contracts.Playground.PlaygroundSheetSummaryDto>> ListAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/sheets", Json.IReadOnlyListPlaygroundSheetSummaryDto, ct);

    public Task<InsightFlow.Contracts.Playground.PlaygroundSheetDto> GetAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/sheets/{id}", Json.PlaygroundSheetDto, ct);

    public Task<InsightFlow.Contracts.Playground.PlaygroundSheetDto> CreateAsync(string? name, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/sheets", JsonBody(new InsightFlow.Contracts.Playground.CreatePlaygroundSheetRequest(name), Json.CreatePlaygroundSheetRequest), Json.PlaygroundSheetDto, ct);

    public Task<InsightFlow.Contracts.Playground.PlaygroundSheetDto> SaveAsync(Guid id, InsightFlow.Contracts.Playground.SavePlaygroundSheetRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Base}/sheets/{id}", JsonBody(request, Json.SavePlaygroundSheetRequest), Json.PlaygroundSheetDto, ct);

    public Task<InsightFlow.Contracts.Playground.PlaygroundSheetDto> DuplicateAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/sheets/{id}/duplicate", content: null, Json.PlaygroundSheetDto, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/sheets/{id}", content: null, ct);

    public Task<InsightFlow.Contracts.Playground.ExportToPlaygroundResult> ExportAsync(InsightFlow.Contracts.Playground.ExportToPlaygroundRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/export", JsonBody(request, Json.ExportToPlaygroundRequest), Json.ExportToPlaygroundResult, ct);
}
