namespace QuickJack.App.Search;

/// <summary>
/// Subsequence matching with the bonuses that make a launcher feel right: "fd" should find
/// "Flush DNS", and an exact prefix should beat a scattered match.
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchScore = 16;
    private const int ConsecutiveBonus = 24;
    private const int WordStartBonus = 20;
    private const int PrefixBonus = 40;
    private const int GapPenalty = 2;

    /// <summary>Score for <paramref name="query"/> against <paramref name="text"/>, or null if it does not match.</summary>
    public static int? Score(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        if (string.IsNullOrEmpty(text)) return null;

        var score = 0;
        var textIndex = 0;
        var previousMatch = -2;

        foreach (var raw in query)
        {
            if (char.IsWhiteSpace(raw)) continue;

            var target = char.ToLowerInvariant(raw);
            var found = -1;

            for (var i = textIndex; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != target) continue;
                found = i;
                break;
            }

            if (found < 0) return null;

            score += MatchScore;
            if (found == previousMatch + 1) score += ConsecutiveBonus;
            if (found == 0) score += PrefixBonus;
            else if (IsWordStart(text, found)) score += WordStartBonus;

            score -= Math.Min(found - textIndex, 8) * GapPenalty;

            previousMatch = found;
            textIndex = found + 1;
        }

        // Prefer the tighter of two equally-good matches.
        return score - text.Length / 8;
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0) return true;

        var previous = text[index - 1];
        if (previous is ' ' or '-' or '_' or '.' or '/' or '\\' or ':') return true;

        // camelCase boundary
        return char.IsLower(previous) && char.IsUpper(text[index]);
    }

    /// <summary>Best score across several fields, weighted so a name match wins over a description match.</summary>
    public static int? ScoreBest(string query, string name, string? description, string? group)
    {
        var best = Score(name, query);

        if (Score(description ?? string.Empty, query) is { } d)
            best = Math.Max(best ?? int.MinValue, d - 30);

        if (Score(group ?? string.Empty, query) is { } g)
            best = Math.Max(best ?? int.MinValue, g - 20);

        return best;
    }
}
