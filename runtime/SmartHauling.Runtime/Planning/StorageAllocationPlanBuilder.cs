using NSMedieval;

namespace SmartHauling.Runtime;

internal static class StorageAllocationPlanBuilder
{
    public static IReadOnlyList<StockpileStorageAllocation> BuildFromCandidates(
        IEnumerable<StorageCandidatePlanner.StorageCandidate> candidates,
        int requestedAmount)
    {
        if (candidates == null || requestedAmount <= 0)
        {
            return Array.Empty<StockpileStorageAllocation>();
        }

        var allocations = new List<StockpileStorageAllocation>();
        var remaining = requestedAmount;
        foreach (var candidate in candidates)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (candidate?.Storage == null || candidate.Storage.HasDisposed)
            {
                continue;
            }

            var allocationAmount = Math.Min(candidate.EstimatedCapacity, remaining);
            if (allocationAmount <= 0)
            {
                continue;
            }

            allocations.Add(new StockpileStorageAllocation(candidate.Storage, allocationAmount));
            remaining -= allocationAmount;
        }

        return MergeAllocations(allocations);
    }

    public static IReadOnlyList<StockpileStorageAllocation> MergeAllocations(
        IEnumerable<StockpileStorageAllocation>? allocations)
    {
        if (allocations == null)
        {
            return Array.Empty<StockpileStorageAllocation>();
        }

        var merged = new List<StockpileStorageAllocation>();
        var indexByStorage = new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);
        foreach (var allocation in allocations)
        {
            if (allocation?.Storage == null ||
                allocation.Storage.HasDisposed ||
                allocation.RequestedAmount <= 0)
            {
                continue;
            }

            if (indexByStorage.TryGetValue(allocation.Storage, out var index))
            {
                var existing = merged[index];
                merged[index] = new StockpileStorageAllocation(
                    existing.Storage,
                    existing.RequestedAmount + allocation.RequestedAmount);
                continue;
            }

            indexByStorage[allocation.Storage] = merged.Count;
            merged.Add(new StockpileStorageAllocation(allocation.Storage, allocation.RequestedAmount));
        }

        return merged;
    }
}
