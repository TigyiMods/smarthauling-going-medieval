namespace SmartHauling.Runtime;

internal static class PlayerForcedPriorityPlanner
{
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
