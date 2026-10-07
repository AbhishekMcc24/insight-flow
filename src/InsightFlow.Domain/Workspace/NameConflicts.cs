namespace InsightFlow.Domain.Workspace;

/// <summary>
/// Resolves name collisions the way desktop file managers do: <c>sales.csv</c>, <c>sales (2).csv</c>,
/// <c>sales (3).csv</c>… Used when a dropped file or folder already exists in the target folder.
/// </summary>
public static class NameConflicts
{
    public const int MaxAttempts = 10_000;

    public static ItemName NextAvailable(ItemName desired, IEnumerable<ItemName> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var taken = existing.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(desired.Key))
        {
            return desired;
        }

        for (var n = 2; n <= MaxAttempts; n++)
        {
            var candidate = desired.WithCounter(n);
            if (!taken.Contains(candidate.Key))
            {
                return candidate;
            }
        }

        throw new DomainRuleException("name_conflict_unresolved", $"Could not find a free name for '{desired}'.");
    }
}
