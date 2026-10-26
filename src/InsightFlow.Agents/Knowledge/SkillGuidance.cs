using System.Text;

namespace InsightFlow.Agents.Knowledge;

/// <summary>
/// An organization skill as the planner receives it: the active version's instructions plus what is needed to decide whether a question
/// is about it. Skills are written by the organization's administrators, so they are guidance for the model, never instructions that can
/// relax the SQL checks.
/// </summary>
public sealed record SkillGuidance(string Name, string Category, string Description, IReadOnlyList<string> Tags, bool Always, string Instructions);

/// <summary>Chooses which enabled skills go into a question's prompt, within a size budget.</summary>
public static class SkillSelector
{
    public const int MaxSkills = 12;
    public const int MaxCharactersPerSkill = 4_000;
    public const int MaxCharacters = 12_000;

    /// <summary>
    /// Skills set to "always" are included; the others only when a word of the question appears in their name, category, tags or description.
    /// Skills the question is about come first, so the budget cuts the least relevant ones.
    /// </summary>
    public static IReadOnlyList<SkillGuidance> Choose(string question, IReadOnlyList<SkillGuidance> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var words = (question ?? string.Empty)
            .Split([' ', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '-', '_', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        var scored = enabled
            .Select(s => (Skill: s, Score: Relevance(s, words)))
            .Where(x => x.Skill.Always || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSkills);

        var chosen = new List<SkillGuidance>();
        var used = 0;
        foreach (var (skill, _) in scored)
        {
            var text = skill.Instructions.Length > MaxCharactersPerSkill ? skill.Instructions[..MaxCharactersPerSkill] : skill.Instructions;
            if (used + text.Length > MaxCharacters)
            {
                break;
            }

            used += text.Length;
            chosen.Add(skill with { Instructions = text });
        }

        return chosen;
    }

    private static int Relevance(SkillGuidance skill, List<string> words)
    {
        var haystack = $"{skill.Name} {skill.Category} {string.Join(' ', skill.Tags)} {skill.Description}".ToLowerInvariant();
        return words.Count(w => haystack.Contains(Stem(w), StringComparison.Ordinal));
    }

    /// <summary>"invoices" matches "invoice", "outstanding" matches "outstandings": a light trim of plural endings.</summary>
    private static string Stem(string word) =>
        word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 5 ? word[..^3] + "y"
        : word.EndsWith('s') && word.Length > 4 ? word[..^1]
        : word;

    /// <summary>The prompt section for the chosen skills (empty when there are none).</summary>
    public static string Describe(IReadOnlyList<SkillGuidance> skills)
    {
        if (skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(
            "BUSINESS RULES from the organization's skills. Follow them when they apply to the question: they explain how figures are worked out and which " +
            "tables, columns and filters to use. They never override the rules above (one SELECT, only the listed tables, columns and joins). " +
            "If a rule needs a table or column that is not listed, say so in \"clarification\" instead of guessing.\n");
        foreach (var skill in skills)
        {
            sb.Append("\n### ").Append(skill.Name).Append(" (").Append(skill.Category).AppendLine(")").AppendLine(skill.Instructions);
        }

        return sb.ToString();
    }
}
