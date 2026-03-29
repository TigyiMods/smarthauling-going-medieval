namespace SmartHauling.Runtime.Infrastructure.World;

internal static class CentralHaulSourceFilter
{
    internal static IReadOnlyCollection<TPile> MergeCandidates<TPile>(
        IEnumerable<TPile> preferredCandidates,
        IEnumerable<TPile> knownCandidates,
        Func<TPile, bool> shouldIncludeKnownCandidate,
        IEqualityComparer<TPile>? comparer = null)
    {
        if (preferredCandidates == null)
        {
            throw new ArgumentNullException(nameof(preferredCandidates));
        }

        if (knownCandidates == null)
        {
            throw new ArgumentNullException(nameof(knownCandidates));
        }

        if (shouldIncludeKnownCandidate == null)
        {
            throw new ArgumentNullException(nameof(shouldIncludeKnownCandidate));
        }

        var merged = comparer != null
            ? new HashSet<TPile>(comparer)
            : new HashSet<TPile>();
        foreach (var candidate in preferredCandidates)
        {
            merged.Add(candidate);
        }

        foreach (var candidate in knownCandidates.Where(shouldIncludeKnownCandidate))
        {
            merged.Add(candidate);
        }

        return merged;
    }

    internal static IReadOnlyList<TPile> FilterWithSingleStorageSnapshot<TPile, TStorageSnapshot>(
        IEnumerable<TPile> candidatePiles,
        Func<IReadOnlyList<TStorageSnapshot>> getStorageSnapshot,
        Func<TPile, IReadOnlyList<TStorageSnapshot>, bool> canUseCandidate)
    {
        if (candidatePiles == null)
        {
            throw new ArgumentNullException(nameof(candidatePiles));
        }

        if (getStorageSnapshot == null)
        {
            throw new ArgumentNullException(nameof(getStorageSnapshot));
        }

        if (canUseCandidate == null)
        {
            throw new ArgumentNullException(nameof(canUseCandidate));
        }

        var storageSnapshot = getStorageSnapshot() ?? Array.Empty<TStorageSnapshot>();

        return candidatePiles
            .Where(candidate => canUseCandidate(candidate, storageSnapshot))
            .ToList();
    }
}
