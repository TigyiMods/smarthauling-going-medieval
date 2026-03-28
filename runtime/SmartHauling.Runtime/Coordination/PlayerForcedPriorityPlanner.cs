namespace SmartHauling.Runtime;

internal static class PlayerForcedPriorityPlanner
{
    public static T? SelectPreferredAnchor<T>(
        T? preferredAnchor,
        IEnumerable<T>? candidates,
        Func<T, bool> isValid,
        Func<T, float> getScore,
        IEqualityComparer<T>? comparer = null)
        where T : class
    {
        if (preferredAnchor != null && isValid(preferredAnchor))
        {
            return preferredAnchor;
        }

        if (candidates == null)
        {
            return null;
        }

        var distinctComparer = comparer ?? EqualityComparer<T>.Default;
        var seen = new HashSet<T>(distinctComparer);
        T? best = null;
        var bestScore = float.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate == null ||
                !seen.Add(candidate) ||
                !isValid(candidate))
            {
                continue;
            }

            var score = getScore(candidate);
            if (best == null || score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    public static bool ContainsCandidate<T>(
        T candidate,
        IEnumerable<T>? candidates,
        IEqualityComparer<T>? comparer = null)
    {
        if (candidate == null || candidates == null)
        {
            return false;
        }

        var distinctComparer = comparer ?? EqualityComparer<T>.Default;
        foreach (var current in candidates)
        {
            if (distinctComparer.Equals(current, candidate))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<T> MergePriorityOrder<T>(
        T anchor,
        IEnumerable<T>? existing,
        IEqualityComparer<T>? comparer = null)
    {
        var distinctComparer = comparer ?? EqualityComparer<T>.Default;
        var merged = new List<T> { anchor };
        if (existing == null)
        {
            return merged;
        }

        foreach (var candidate in existing)
        {
            if (merged.Contains(candidate, distinctComparer))
            {
                continue;
            }

            merged.Add(candidate);
        }

        return merged;
    }

    public static IReadOnlyList<T> SelectLocalPrioritySeeds<T>(
        T anchor,
        IEnumerable<T>? candidates,
        float maxDistance,
        Func<T, bool> isValid,
        Func<T, float> getDistanceFromAnchor,
        IEqualityComparer<T>? comparer = null)
    {
        var distinctComparer = comparer ?? EqualityComparer<T>.Default;
        var selected = new List<T> { anchor };
        if (candidates == null)
        {
            return selected;
        }

        foreach (var candidate in candidates)
        {
            if (selected.Contains(candidate, distinctComparer) ||
                !isValid(candidate) ||
                getDistanceFromAnchor(candidate) > maxDistance)
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected;
    }
}
