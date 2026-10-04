namespace DMCBK.Core.Beacon;

/// <summary>
/// Shared edit-distance helpers for did-you-mean suggestions.
/// Consolidates the three Levenshtein copies (parser, interpreter, game tables) into one two-row implementation so suggestion behavior can never drift.
/// </summary>
internal static class BeaconTextDistance
{
    /// <summary>Computes the Levenshtein edit distance between <paramref name="a"/> and <paramref name="b"/>.</summary>
    internal static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0)
            return b.Length;

        if (b.Length == 0)
            return a.Length;

        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Returns the nearest candidate within <paramref name="maxDistance"/>, or null when nothing is close.
    /// Comparison is case-insensitive; the returned value keeps its original casing.
    /// </summary>
    internal static string? SuggestNearest(string word, IEnumerable<string> candidates, int maxDistance = 3)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(candidates);
        string lower = word.ToLowerInvariant();
        string? best = null;
        int bestDistance = maxDistance;
        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(lower, candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }
}
