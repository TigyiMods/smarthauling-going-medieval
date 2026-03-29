using NSMedieval;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal sealed class StockpileMixedSourcePlanResult
{
    public StockpileMixedSourcePlanResult(int addedCount, IReadOnlyList<string> details)
    {
        AddedCount = addedCount;
        Details = details;
    }

    public int AddedCount { get; }

    public IReadOnlyList<string> Details { get; }
}

internal static class StockpileMixedSourcePlanner
{
    public static StockpileMixedSourcePlanResult Apply(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        List<ResourcePileInstance> plannedPiles,
        List<TargetObject> queue,
        ResourcePileInstance firstPile,
        IStorage firstStorage,
        IReadOnlyCollection<IStorage> preferredDestinationOrder,
        Storage workerStorage,
        int pickupBudget,
        ref float plannedWeight,
        ref bool plannedAny,
        ref int totalPlanned,
        Dictionary<string, int> requestedByResourceId,
        Dictionary<ResourcePileInstance, int> plannedAmountsByPile,
        float mixedGroundHarvestExtent,
        Func<StockpileHaulingGoal, Resource, int> getOptimisticPickupBudget)
    {
        var mixedAdded = 0;
        var mixedDetails = new List<string>();
        if (totalPlanned >= pickupBudget)
        {
            return new StockpileMixedSourcePlanResult(mixedAdded, mixedDetails);
        }

        var plannedPileSet = new HashSet<ResourcePileInstance>(plannedPiles, ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var mixedCandidates = RuntimeServices.WorldSnapshot.GetAllKnownPileInstances()
            .Where(pile =>
                !plannedPileSet.Contains(pile) &&
                !pile.HasDisposed &&
                IsNearPlannedSourcePatch(plannedPiles, pile, mixedGroundHarvestExtent))
            .ToList();

        var remainingCandidates = mixedCandidates;
        while (remainingCandidates.Count > 0)
        {
            var currentAnchor = plannedPiles.LastOrDefault()?.GetPosition() ?? firstPile.GetPosition();
            var pile = remainingCandidates
                .OrderBy(candidate => StockpilePileTopology.GetNearestPatchDistance(plannedPiles, candidate))
                .ThenBy(candidate => Vector3.Distance(currentAnchor, candidate.GetPosition()))
                .ThenBy(candidate => Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition()))
                .First();
            remainingCandidates.Remove(pile);

            var storedResource = pile.GetStoredResource();
            if (storedResource == null || storedResource.HasDisposed)
            {
                continue;
            }

            if (!HaulSourcePolicy.CanReachPile(goal, pile))
            {
                CaptureDetail(mixedDetails, $"{pile.BlueprintId}:reach");
                continue;
            }

            if (!CanConsiderMixedPile(goal, creature, pile, firstStorage, preferredDestinationOrder, getOptimisticPickupBudget, out var mixedRejection, out var compatibleStorage))
            {
                CaptureDetail(mixedDetails, $"{pile.BlueprintId}:{mixedRejection}");
                continue;
            }

            if (!TryPlanAdditionalPile(goal, queue, pile, workerStorage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
            {
                CaptureDetail(mixedDetails, $"{pile.BlueprintId}:capacity");
                continue;
            }

            mixedAdded++;
            plannedPiles.Add(pile);
            CaptureDetail(mixedDetails, $"{pile.BlueprintId}@{compatibleStorage?.Priority.ToString() ?? "None"}");

            if (totalPlanned >= pickupBudget)
            {
                break;
            }
        }

        return new StockpileMixedSourcePlanResult(mixedAdded, mixedDetails);
    }

    internal static bool TryPlanAdditionalPile(
        Goal goal,
        List<TargetObject> queue,
        ResourcePileInstance pile,
        Storage storage,
        ref float plannedWeight,
        ref bool plannedAny,
        ref int totalPlanned,
        int pickupBudget,
        Dictionary<string, int> requestedByResourceId,
        Dictionary<ResourcePileInstance, int> plannedAmountsByPile)
    {
        var storedResource = pile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return false;
        }

        var projected = PickupPlanningUtil.GetProjectedCapacity(storage, storedResource.Blueprint, plannedWeight, plannedAny);
        if (projected <= 0)
        {
            return false;
        }

        projected = Mathf.Min(projected, storedResource.Amount, pickupBudget - totalPlanned);
        if (projected <= 0)
        {
            return false;
        }

        pile.ReserveAll();
        if (!RuntimeServices.Reservations.TryReserveObject(pile, goal.AgentOwner))
        {
            RuntimeServices.Reservations.ReleaseAll(pile);
            return false;
        }

        queue.Add(new TargetObject(pile));
        totalPlanned += projected;
        plannedWeight += PickupPlanningUtil.GetProjectedWeight(storage, storedResource.Blueprint, projected);
        plannedAny = true;
        var resourceId = storedResource.Blueprint.GetID();
        requestedByResourceId[resourceId] = requestedByResourceId.TryGetValue(resourceId, out var currentAmount)
            ? currentAmount + projected
            : projected;
        plannedAmountsByPile[pile] = projected;
        return true;
    }

    private static bool IsNearPlannedSourcePatch(
        IReadOnlyCollection<ResourcePileInstance> plannedPiles,
        ResourcePileInstance candidatePile,
        float mixedGroundHarvestExtent)
    {
        return StockpilePileTopology.GetNearestPatchDistance(plannedPiles, candidatePile) <= mixedGroundHarvestExtent;
    }

    private static bool CanConsiderMixedPile(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance pile,
        IStorage primaryStorage,
        IReadOnlyCollection<IStorage> preferredOrder,
        Func<StockpileHaulingGoal, Resource, int> getOptimisticPickupBudget,
        out string rejection,
        out IStorage? compatibleStorage)
    {
        rejection = "unknown";
        compatibleStorage = null;

        if (!ClusterOwnershipStore.CanUsePile(creature, pile))
        {
            rejection = "claimed";
            return false;
        }

        if (HaulFailureBackoffStore.IsCoolingDown(pile))
        {
            rejection = "cooldown";
            return false;
        }

        var storedResource = pile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            rejection = "empty";
            return false;
        }

        var effectiveSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
        var requestedAmount = Math.Max(1, Math.Min(storedResource.Amount, getOptimisticPickupBudget(goal, storedResource.Blueprint)));
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            effectiveSourcePriority,
            enablePriorityFallback: false,
            requestedAmount,
            preferredStorage: primaryStorage,
            preferredOrder: preferredOrder);

        compatibleStorage = candidatePlan.Primary?.Storage;
        if (compatibleStorage == null || candidatePlan.GetEstimatedCapacityBudget(requestedAmount) <= 0)
        {
            rejection = "dest";
            return false;
        }

        rejection = "ok";
        return true;
    }

    private static void CaptureDetail(List<string> details, string value)
    {
        if (details.Count < 8)
        {
            details.Add(value);
        }
    }
}
