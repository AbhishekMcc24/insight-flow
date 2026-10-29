using InsightFlow.Infrastructure.Connectors;
using InsightFlow.Infrastructure.Connectors.Knowledge;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Repository;
using InsightFlow.Foundation.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Logic.Knowledge;

/// <summary>
/// Application logic of Knowledge Base Configuration: saves the tenant's Neo4j connection (password in the secret store),
/// tests it, and writes an approved <see cref="KnowledgeGraphPlan"/> to Neo4j. A plan from the browser is never trusted: it is
/// reconciled against the tenant's real shared datasets before anything is written. Sys-admin only (enforced by the endpoints).
/// </summary>
public sealed partial class KnowledgeService(
    InsightFlowDbContext db,
    ITenantContext caller,
    ISecretStore secrets,
    IKnowledgeGraphGateway gateway,
    IConnectorRegistry connectors,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<KnowledgeService> logger)
{
    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private bool AllowPrivateAddresses => environment.IsDevelopment();

    public async Task<KnowledgeBaseConfigDto> GetConfigAsync(CancellationToken cancellationToken)
    {
        var config = await db.KnowledgeBaseConfigs.SingleOrDefaultAsync(cancellationToken);
        return config is null
            ? new KnowledgeBaseConfigDto("bolt://localhost:7687", "neo4j", "neo4j", false, false, false, false, null, null, null, null, 0, 0, 0)
            : ToDto(config);
    }

    public async Task<KnowledgeBaseConfigDto> SaveConfigAsync(SaveKnowledgeBaseConfigRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var config = await db.KnowledgeBaseConfigs.SingleOrDefaultAsync(cancellationToken);
        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        if (password is { Length: > 256 })
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_password", "The Neo4j password is too long.");
        }

        if (config is null)
        {
            if (password is null)
            {
                throw new ApiProblemException(StatusCodes.Status400BadRequest, "password_required", "Enter the Neo4j password.");
            }

            config = KnowledgeBaseConfig.Create(Tenant, request.Uri, request.User, request.Database, request.UseAura, request.Encrypted, request.Enabled, now);
            await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);
            db.KnowledgeBaseConfigs.Add(config);
        }
        else
        {
            config.Update(request.Uri, request.User, request.Database, request.UseAura, request.Encrypted, request.Enabled, now);
            await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);
        }

        string? oldSecret = null;
        if (password is not null)
        {
            oldSecret = config.PasswordSecretName;
            config.SetPasswordSecret(await secrets.SaveAsync(Tenant, password, cancellationToken), now);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (oldSecret is not null && SecretReference.TryParse(oldSecret, out var old))
        {
            await secrets.DeleteAsync(Tenant, old, cancellationToken);
        }

        LogConfigSaved(logger, config.Id, config.Enabled);
        return ToDto(config);
    }

    public async Task<KnowledgeTestResultDto> TestAsync(CancellationToken cancellationToken)
    {
        var (config, settings) = await LoadSettingsAsync(requireEnabled: false, cancellationToken);
        await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);
        var result = await gateway.TestAsync(settings, cancellationToken);
        config.RecordTest(result.Succeeded, result.Message, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogTested(logger, config.Id, result.Succeeded);
        return new KnowledgeTestResultDto(result.Succeeded, result.Message, result.ServerAgent);
    }

    public async Task<KnowledgeGraphSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var (config, settings) = await LoadSettingsAsync(requireEnabled: true, cancellationToken);
        await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);
        var summary = await gateway.SummarizeAsync(settings, Tenant.Value, cancellationToken);
        return new KnowledgeGraphSummaryDto(summary.Tables, summary.Columns, summary.Relationships, summary.Lineage);
    }

    /// <summary>Writes the stored definitions to Neo4j, replacing the tenant's previous graph. The graph is built here from the database, never taken from the browser.</summary>
    public async Task<KnowledgeSeedResultDto> PushAsync(CancellationToken cancellationToken)
    {
        var (config, settings) = await LoadSettingsAsync(requireEnabled: true, cancellationToken);
        await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);

        var plan = (await db.LoadDefinitionsAsync(cancellationToken)).ToPlan();
        if (plan.Tables.Count == 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "empty_plan", "The knowledge base has no tables yet. Import or add tables first.");
        }

        var counts = await gateway.ApplyAsync(settings, Tenant.Value, plan, cancellationToken);
        config.RecordSeed(counts.Tables, counts.Columns, counts.Relationships, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return new KnowledgeSeedResultDto(counts.Tables, counts.Columns, counts.Relationships, counts.Lineage);
    }

    private async Task<(KnowledgeBaseConfig Config, KnowledgeConnectionSettings Settings)> LoadSettingsAsync(bool requireEnabled, CancellationToken cancellationToken)
    {
        var config = await db.KnowledgeBaseConfigs.SingleOrDefaultAsync(cancellationToken)
            ?? throw new ApiProblemException(StatusCodes.Status409Conflict, "not_configured", "Save the Neo4j connection first.");
        if (requireEnabled && !config.Enabled)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "disabled", "The knowledge base is switched off. Enable it in the configuration.");
        }

        if (!SecretReference.TryParse(config.PasswordSecretName, out var reference)
            || await secrets.GetAsync(Tenant, reference, cancellationToken) is not { } password)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "password_missing", "No Neo4j password is stored. Enter it and save.");
        }

        return (config, new KnowledgeConnectionSettings(config.Uri, config.User, password, config.Database, config.UseAura, config.Encrypted));
    }

    private static KnowledgeBaseConfigDto ToDto(KnowledgeBaseConfig c) =>
        new(c.Uri, c.User, c.Database, c.UseAura, c.Encrypted, c.Enabled, c.PasswordSecretName is not null,
            c.LastTestedAt, c.LastTestSucceeded, c.LastTestMessage, c.LastSeededAt, c.LastSeedTables, c.LastSeedColumns, c.LastSeedRelationships);

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base {ConfigId} saved (enabled {Enabled})")]
    private static partial void LogConfigSaved(ILogger logger, Guid configId, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base {ConfigId} connection test succeeded: {Succeeded}")]
    private static partial void LogTested(ILogger logger, Guid configId, bool succeeded);
}
