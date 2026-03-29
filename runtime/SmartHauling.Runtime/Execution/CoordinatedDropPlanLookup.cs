using NSMedieval;
using NSMedieval.Goap;

namespace SmartHauling.Runtime;

internal static class CoordinatedDropPlanLookup
{
    public static bool TryGetPlannedAllocations(Goal goal, string resourceId, out IReadOnlyList<StockpileStorageAllocation> allocations)
    {
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task) &&
            task.TryGetDropPlan(resourceId, out var dropPlan))
        {
            allocations = dropPlan.GetActiveAllocations();
            if (allocations.Count > 0)
            {
                return true;
            }
        }

        if (StockpileDestinationPlanStore.TryGetActiveAllocations(goal, resourceId, out allocations) &&
            allocations.Count > 0)
        {
            return true;
        }

        allocations = null!;
        return false;
    }

    public static bool TryGetPlannedStorages(Goal goal, string resourceId, out IReadOnlyList<IStorage> storages)
    {
        if (TryGetPlannedAllocations(goal, resourceId, out var allocations))
        {
            storages = allocations
                .Select(allocation => allocation.Storage)
                .ToList();
            if (storages.Count > 0)
            {
                return true;
            }
        }

        if (StockpileDestinationPlanStore.TryGetActiveStorages(goal, resourceId, out storages) &&
            storages.Count > 0)
        {
            return true;
        }

        storages = null!;
        return false;
    }
}
