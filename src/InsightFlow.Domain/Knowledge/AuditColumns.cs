namespace InsightFlow.Domain.Knowledge;

/// <summary>
/// Recognises audit columns: who created, changed, deleted or cancelled a row (<c>created_user</c>, <c>modified_by</c>, <c>cancelled_by</c>…).
/// A relationship that links such a column to a user table says "this row was written by that person", not "these rows belong together", so it
/// must never carry a join between two business tables. Pure, so it is unit-tested.
/// </summary>
public static class AuditColumns
{
    private static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        "created", "modified", "updated", "deleted", "cancelled", "canceled", "approved", "rejected", "inserted", "changed", "posted",
        "verified", "reviewed", "entered", "authorized", "authorised", "closed", "submitted", "added", "edited", "removed",
    };

    private static readonly HashSet<string> Actors = new(StringComparer.OrdinalIgnoreCase)
    {
        "by", "user", "usr", "uid", "userid", "emp", "employee", "staff", "person", "operator",
    };

    /// <summary>True for names such as <c>mo_created_user</c>, <c>ih_cancelled_by</c> or <c>modifiedBy</c>: an action word together with a "who" word.</summary>
    public static bool IsAuditColumn(string? column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return false;
        }

        var tokens = Tokens(column);
        return tokens.Any(Actions.Contains) && tokens.Any(Actors.Contains);
    }

    /// <summary>A relationship is an audit relationship when either of its columns is an audit column.</summary>
    public static bool IsAuditRelationship(string? fromColumn, string? toColumn) => IsAuditColumn(fromColumn) || IsAuditColumn(toColumn);

    private static List<string> Tokens(string name)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            var boundary = !char.IsLetterOrDigit(ch) || (char.IsUpper(ch) && i > 0 && char.IsLower(name[i - 1]));
            if (boundary && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }

            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
