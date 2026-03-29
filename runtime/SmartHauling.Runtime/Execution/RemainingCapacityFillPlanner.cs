using NSMedieval;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class RemainingCapacityFillPlanner
{
    private const float RemainingCapacityFillExtent = 16f;
    private const int MaxFillCandidatesToInspect = 24;

    public static bool TryAppend(StockpileHaulingGoal goal, CreatureBase creature, Storage storage, bool prependToFront)
    {
        if (storage == null || storage.GetFreeSpace() <= 0f)
        {
            return false;
        }

        var reservedOrQueuedPiles = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var currentPickup = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
        if (currentPickup != null && !currentPickup.HasDisposed)
        {
            reservedOrQueuedPiles.Add(currentPickup);
        }

        foreach (var queuedPile in goal.GetTargetQueue(TargetIndex.A)
                     .Select(target => target.GetObjectAs<ResourcePileInstance>())
                     .Where(pile => pile != null && !pile.HasDisposed))
        {
            reservedOrQueuedPiles.Add(queuedPile!);
        }

        DynamicFillCandidate? bestCandidate = null;
        var fillAnchorPiles = GetFillAnchorPiles(goal);
        var fillAnchorPositions = fillAnchorPiles
            .Where(pile => pile != null && !pile.HasDisposed)
            .Select(pile => pile.GetPosition())
            .ToList();
        var hasFallbackAnchor = CoordinatedStockpileExecutionStore.TryGetLastPickupPosition(goal, out var fallbackAnchorPosition);

        foreach (var pile in HaulingDecisionTracePatch.GetHaulablePileSnapshot()
                     .Where(pile => pile != null && !pile.HasDisposed)
                     .OrderBy(pile => GetFillAnchorDistance(fillAnchorPositions, hasFallbackAnchor ? fallbackAnchorPosition : (Vector3?)null, pile))
                     .ThenBy(pile => Vector3.Distance(creature.GetPosition(), pile.GetPosition()))
                     .Take(MaxFillCandidatesToInspect))
        {
            var patchDistance = GetFillAnchorDistance(fillAnchorPositions, hasFallbackAnchor ? fallbackAnchorPosition : (Vector3?)null, pile);
            if (reservedOrQueuedPiles.Contains(pile) ||
                Vector3.Distance(creature.GetPosition(), pile.GetPosition()) > RemainingCapacityFillExtent ||
                patchDistance > RemainingCapacityFillExtent ||
                HaulFailureBackoffStore.IsCoolingDown(pile) ||
                !ClusterOwnershipStore.CanUsePile(creature, pile) ||
                !HaulSourcePolicy.CanReachPile(goal, pile) ||
                !HaulSourcePolicy.ValidatePile(goal, pile))
            {
                continue;
            }

            var storedResource = pile.GetStoredResource();
            if (storedResource == null || storedResource.HasDisposed || storedResource.Amount <= 0)
            {
                continue;
            }

            if (!CanAppendResource(goal, storage, storedResource.Blueprint))
            {
                continue;
            }

            var requestedAmount = PickupPlanningUtil.GetProjectedCapacity(
                storage,
                storedResource.Blueprint,
                0f,
                storage.HasOneOrMoreResources());
            requestedAmount = System.Math.Min(requestedAmount, storedResource.Amount);
            if (requestedAmount <= 0)
            {
                continue;
            }

            if (!TryResolveFillDestinationPlan(
                    goal,
                    creature,
                    storedResource,
                    pile,
                    requestedAmount,
                    out var effectiveRequestedAmount,
                    out var orderedStorages,
                    out var plannedAllocations))
            {
                continue;
            }

            var candidate = new DynamicFillCandidate(
                pile,
                storedResource.Blueprint,
                effectiveRequestedAmount,
                orderedStorages,
                plannedAllocations,
                Vector3.Distance(creature.GetPosition(), pile.GetPosition()),
                patchDistance,
                CoordinatedDropPlanLookup.TryGetPlannedStorages(goal, storedResource.BlueprintId, out _));
            if (bestCandidate == null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            return false;
        }

        bestCandidate.Pile.ReserveAll();
        if (!RuntimeServices.Reservations.TryReserveObject(bestCandidate.Pile, goal.AgentOwner))
        {
            RuntimeServices.Reservations.ReleaseAll(bestCandidate.Pile);
            return false;
        }

        var queueTarget = new TargetObject(bestCandidate.Pile);
        if (prependToFront)
        {
            goal.GetTargetQueue(TargetIndex.A).Insert(0, queueTarget);
        }
        else
        {
            goal.GetTargetQueue(TargetIndex.A).Add(queueTarget);
        }

        StockpileDestinationPlanStore.MergeResourcePlan(
            goal,
            bestCandidate.Blueprint.GetID(),
            bestCandidate.OrderedStorages,
            bestCandidate.RequestedAmount,
            bestCandidate.PlannedAllocations);
        if (CarrySummaryUtil.Snapshot(storage).Any(resource => resource.BlueprintId != bestCandidate.Blueprint.GetID()))
        {
            MixedCollectPlanStore.MarkStartedMixed(goal);
        }

        MixedCollectPlanStore.AppendRequestedAmount(goal, bestCandidate.Blueprint, bestCandidate.RequestedAmount);

        DiagnosticTrace.Info(
            "coord.exec",
            $"Extended pickup plan for {goal.AgentOwner}: resource={bestCandidate.Blueprint.GetID()}, requested={bestCandidate.RequestedAmount}, free={storage.GetFreeSpace():0.##}, distance={bestCandidate.Distance:0.0}, patchDistance={bestCandidate.PatchDistance:0.0}, prepend={prependToFront}",
            120);
        return true;
    }

    public static void RememberCurrentPickupAnchor(StockpileHaulingGoal goal)
    {
        var pile = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
        if (pile == null || pile.HasDisposed)
        {
            return;
        }

        CoordinatedStockpileExecutionStore.RememberPickupPosition(goal, pile.GetPosition());
    }

    private static bool TryResolveFillDestinationPlan(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourceInstance storedResource,
        ResourcePileInstance pile,
        int requestedAmount,
        out int effectiveRequestedAmount,
        out IReadOnlyList<IStorage> orderedStorages,
        out IReadOnlyList<StockpileStorageAllocation> plannedAllocations)
    {
        effectiveRequestedAmount = requestedAmount;
        if (CoordinatedDropPlanLookup.TryGetPlannedAllocations(goal, storedResource.BlueprintId, out plannedAllocations) &&
            plannedAllocations.Count > 0)
        {
            orderedStorages = plannedAllocations.Select(allocation => allocation.Storage).ToList();
            return orderedStorages.Count > 0;
        }

        if (CoordinatedDropPlanLookup.TryGetPlannedStorages(goal, storedResource.BlueprintId, out orderedStorages) &&
            orderedStorages.Count > 0)
        {
            plannedAllocations = Array.Empty<StockpileStorageAllocation>();
            return true;
        }

        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            sourcePriority,
            enablePriorityFallback: false,
            requestedAmount);
        if (candidatePlan.Primary == null || candidatePlan.GetEstimatedCapacityBudget(requestedAmount) <= 0)
        {
            orderedStorages = null!;
            plannedAllocations = null!;
            return false;
        }

        var plannedAmount = candidatePlan.GetEstimatedCapacityBudget(requestedAmount);
        effectiveRequestedAmount = plannedAmount;
        plannedAllocations = StorageAllocationPlanBuilder.BuildFromCandidates(candidatePlan.Candidates, plannedAmount);
        orderedStorages = plannedAllocations.Count > 0
            ? plannedAllocations.Select(allocation => allocation.Storage).ToList()
            : candidatePlan.OrderedStorages;
        return orderedStorages.Count > 0;
    }

    private static bool CanAppendResource(Goal goal, Storage storage, Resource blueprint)
    {
        if (storage == null || blueprint == null)
        {
            return false;
        }

        var carriedResources = CarrySummaryUtil.Snapshot(storage);
        if (carriedResources.Count == 0)
        {
            return true;
        }

        var hasMixedPlan = MixedCollectPlanStore.HasMixedPlan(goal);
        if (!hasMixedPlan && carriedResources.Any(resource => resource.Blueprint != blueprint))
        {
            // Allow introducing one additional resource type when currently carrying a single type.
            // The mixed-plan flag will be set when this candidate is appended.
            return carriedResources.Count == 1;
        }

        var singleResource = storage.GetSingleResource();
        return hasMixedPlan || singleResource == null || singleResource.Blueprint == blueprint;
    }

    private static IReadOnlyList<ResourcePileInstance> GetFillAnchorPiles(Goal goal)
    {
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
        {
            var planned = task.PlannedPickups
                .Where(pile => pile != null && !pile.HasDisposed)
                .ToList();
            if (planned.Count > 0)
            {
                return planned;
            }
        }

        var queued = goal.GetTargetQueue(TargetIndex.A)
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .Where(pile => pile != null && !pile.HasDisposed)
            .Cast<ResourcePileInstance>()
            .ToList();
        if (queued.Count > 0)
        {
            return queued;
        }

        var current = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
        if (current != null && !current.HasDisposed)
        {
            return new[] { current };
        }

        return System.Array.Empty<ResourcePileInstance>();
    }

    private static float GetFillAnchorDistance(
        IReadOnlyList<Vector3> anchorPositions,
        Vector3? fallbackAnchorPosition,
        ResourcePileInstance candidatePile)
    {
        if (candidatePile == null || candidatePile.HasDisposed)
        {
            return float.MaxValue;
        }

        return LocalFillPlanner.GetAnchorDistance(
            anchorPositions,
            fallbackAnchorPosition,
            candidatePile.GetPosition());
    }

    private sealed class DynamicFillCandidate
    {
        public DynamicFillCandidate(
            ResourcePileInstance pile,
            Resource blueprint,
            int requestedAmount,
            IReadOnlyList<IStorage> orderedStorages,
            IReadOnlyList<StockpileStorageAllocation> plannedAllocations,
            float distance,
            float patchDistance,
            bool hasExistingDropPlan)
        {
            Pile = pile;
            Blueprint = blueprint;
            RequestedAmount = requestedAmount;
            OrderedStorages = orderedStorages;
            PlannedAllocations = plannedAllocations;
            Distance = distance;
            PatchDistance = patchDistance;
            HasExistingDropPlan = hasExistingDropPlan;
        }

        public ResourcePileInstance Pile { get; }

        public Resource Blueprint { get; }

        public int RequestedAmount { get; }

        public IReadOnlyList<IStorage> OrderedStorages { get; }

        public IReadOnlyList<StockpileStorageAllocation> PlannedAllocations { get; }

        public float Distance { get; }

        public float PatchDistance { get; }

        public bool HasExistingDropPlan { get; }

        public float Score => LocalFillPlanner.CalculateCandidateScore(
            RequestedAmount,
            Distance,
            PatchDistance,
            HasExistingDropPlan);
    }
}
