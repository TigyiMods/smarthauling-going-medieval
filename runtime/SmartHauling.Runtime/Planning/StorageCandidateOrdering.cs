using NSMedieval;

namespace SmartHauling.Runtime;

internal static class StorageCandidateOrdering
{
    public static IReadOnlyList<StorageCandidatePlanner.StorageCandidate> OrderCandidates(
        IEnumerable<StorageCandidatePlanner.StorageCandidate> candidates,
        IStorage? preferredStorage)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Storage.Priority)
            .ThenBy(candidate => candidate.PreferredOrderRank)
            .ThenByDescending(candidate => candidate.FitRatio >= 0.999f)
            .ThenByDescending(candidate => candidate.EstimatedCapacity)
            .ThenBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => preferredStorage != null && ReferenceEquals(candidate.Storage, preferredStorage))
            .ToList();
    }
}
