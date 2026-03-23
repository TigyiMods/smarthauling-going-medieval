using NSMedieval;
using NSMedieval.Goap;

namespace SmartHauling.Runtime;

internal static class CoordinatedDropPlanLookup
{
    public static bool TryGetPlannedStorages(Goal goal, string resourceId, out IReadOnlyList<IStorage> storages)
    {
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task) &&
            task.TryGetDropPlan(resourceId, out var dropPlan))
        {
            storages = dropPlan.GetActiveStorages();
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
