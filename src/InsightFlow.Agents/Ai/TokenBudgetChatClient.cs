using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace InsightFlow.Agents.Ai;

/// <summary>Thrown when a tenant has used its monthly token budget. Mapped to 429 by AgentService.</summary>
public sealed class TokenBudgetExceededException : Exception
{
    public TokenBudgetExceededException()
    {
    }

    public TokenBudgetExceededException(string message)
        : base(message)
    {
    }

    public TokenBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Monthly token counters per tenant.</summary>
public interface ITokenUsageStore
{
    Task<long> GetUsedAsync(TenantId tenant, string month, CancellationToken cancellationToken);

    Task AddAsync(TenantId tenant, string month, long tokens, CancellationToken cancellationToken);
}

/// <summary>
/// Token counters in the distributed cache (Redis). Read-modify-write is not atomic, so concurrent calls can under-count
/// slightly — acceptable for a spending guard. TODO(dev2): persist usage in PostgreSQL for billing and admin reports.
/// </summary>
internal sealed class DistributedCacheTokenUsageStore(IDistributedCache cache) : ITokenUsageStore
{
    public async Task<long> GetUsedAsync(TenantId tenant, string month, CancellationToken cancellationToken)
    {
        var value = await cache.GetStringAsync(Key(tenant, month), cancellationToken);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) ? used : 0;
    }

    public async Task AddAsync(TenantId tenant, string month, long tokens, CancellationToken cancellationToken)
    {
        var used = await GetUsedAsync(tenant, month, cancellationToken);
        await cache.SetStringAsync(
            Key(tenant, month),
            (used + tokens).ToString(CultureInfo.InvariantCulture),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(62) },
            cancellationToken);
    }

    private static string Key(TenantId tenant, string month) => $"insightflow:tokens:{tenant}:{month}";
}

/// <summary>In-process counters (tests and hosts without Redis).</summary>
public sealed class InMemoryTokenUsageStore : ITokenUsageStore
{
    private readonly ConcurrentDictionary<string, long> _used = new(StringComparer.Ordinal);

    public Task<long> GetUsedAsync(TenantId tenant, string month, CancellationToken cancellationToken) =>
        Task.FromResult(_used.GetValueOrDefault($"{tenant}:{month}"));

    public Task AddAsync(TenantId tenant, string month, long tokens, CancellationToken cancellationToken)
    {
        _used.AddOrUpdate($"{tenant}:{month}", tokens, (_, current) => current + tokens);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Enforces a tenant's monthly token budget around any <see cref="IChatClient"/>: refuses calls once the budget is used
/// and records usage (from <see cref="ChatResponse.Usage"/> or streamed <see cref="UsageContent"/>) after each call.
/// </summary>
public sealed class TokenBudgetChatClient(
    IChatClient inner,
    TenantId tenant,
    ModelRoute route,
    long monthlyBudget,
    ITokenUsageStore usage,
    TimeProvider clock) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await EnsureWithinBudgetAsync(cancellationToken);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        await RecordAsync(response.Usage, cancellationToken);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureWithinBudgetAsync(cancellationToken);
        UsageDetails? total = null;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                total ??= new UsageDetails();
                total.Add(content.Details);
            }

            yield return update;
        }

        await RecordAsync(total, cancellationToken);
    }

    private string Month => clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private async Task EnsureWithinBudgetAsync(CancellationToken cancellationToken)
    {
        if (await usage.GetUsedAsync(tenant, Month, cancellationToken) >= monthlyBudget)
        {
            AgentsTelemetry.BudgetRejections.Add(1);
            throw new TokenBudgetExceededException("This workspace has used its AI allowance for the month.");
        }
    }

    private async Task RecordAsync(UsageDetails? details, CancellationToken cancellationToken)
    {
        if (details is null)
        {
            return;
        }

        var input = details.InputTokenCount ?? 0;
        var output = details.OutputTokenCount ?? 0;
        var tokens = details.TotalTokenCount ?? input + output;
        AgentsTelemetry.TokensUsed.Add(input, new KeyValuePair<string, object?>("route", route.Key), new KeyValuePair<string, object?>("direction", "input"));
        AgentsTelemetry.TokensUsed.Add(output, new KeyValuePair<string, object?>("route", route.Key), new KeyValuePair<string, object?>("direction", "output"));
        await usage.AddAsync(tenant, Month, tokens, cancellationToken);
    }
}
