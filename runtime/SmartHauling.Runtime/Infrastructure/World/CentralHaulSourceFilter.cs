using NSMedieval;

namespace SmartHauling.Runtime.Infrastructure.World;

internal static class CentralHaulSourceFilter
{
    internal static IReadOnlyList<TPile> FilterWithSingleStorageSnapshot<TPile>(
        IEnumerable<TPile> candidatePiles,
        Func<IReadOnlyList<IStorage>> getStorageSnapshot,
        Func<TPile, IReadOnlyList<IStorage>, bool> canUseCandidate)
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

        var storageSnapshot = getStorageSnapshot() ?? Array.Empty<IStorage>();

        return candidatePiles
            .Where(candidate => canUseCandidate(candidate, storageSnapshot))
            .ToList();
    }
}
