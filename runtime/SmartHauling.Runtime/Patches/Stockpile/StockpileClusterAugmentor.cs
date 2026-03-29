using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Infrastructure.Reflection;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal sealed class StockpileClusterAugmentResult
{
    public StockpileClusterAugmentResult(
        DestinationPlanOutcome destinationOutcome,
        IReadOnlyDictionary<string, int> requestedByResourceId,
        int totalPlanned)
    {
        DestinationOutcome = destinationOutcome;
        RequestedByResourceId = requestedByResourceId;
        TotalPlanned = totalPlanned;
    }

    public DestinationPlanOutcome DestinationOutcome { get; }

    public IReadOnlyDictionary<string, int> RequestedByResourceId { get; }

    public int TotalPlanned { get; }
}

internal static class StockpileClusterAugmentor
{
    public static StockpileClusterAugmentResult Apply(
        StockpileHaulingGoal goal,
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sourcePatchPiles,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        int destinationCapacityBudget,
        float sourceClusterExtent,
        float patchSweepExtent,
        float patchSweepLinkExtent,
        float mixedGroundHarvestExtent,
        int patchSweepCountThreshold,
        int patchSweepAmountThreshold,
        float minimumDetourBudget,
        float maximumDetourBudget,
        float detourBudgetMultiplier,
        Func<StockpileHaulingGoal, Resource, int> getOptimisticPickupBudget,
        Action<Goal, TargetIndex> clearTargetsQueue,
        Action<Goal, TargetIndex, TargetObject> queueTarget)
    {
        if (goal.AgentOwner is not CreatureBase creature || goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return new StockpileClusterAugmentResult(
                DestinationPlanOutcome.Failed("missing-agent"),
                new Dictionary<string, int>(),
                0);
        }

        var queue = goal.GetTargetQueue(TargetIndex.A);
        var queuedPiles = queue
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .Where(pile => pile != null)
            .Cast<ResourcePileInstance>()
            .ToList();

        if (!queuedPiles.Contains(firstPile))
        {
            queuedPiles.Insert(0, firstPile);
        }

        var knownPiles = new HashSet<ResourcePileInstance>(queuedPiles);
        var sameTypeSweep = StockpileSameTypeSweepPlanner.Build(
            goal,
            creature,
            firstPile,
            sourcePatchPiles.Where(pile => !knownPiles.Contains(pile)).ToList(),
            firstStorage,
            preferredDestinationOrder,
            sourceClusterExtent,
            patchSweepExtent,
            patchSweepLinkExtent,
            patchSweepAmountThreshold,
            patchSweepCountThreshold,
            minimumDetourBudget,
            maximumDetourBudget,
            detourBudgetMultiplier);

        var orderedCandidates = sameTypeSweep.OrderedCandidates;

        var optimisticPickupBudget = getOptimisticPickupBudget(goal, firstPile.Blueprint);
        // Do not cap pickup budget by the seed resource destination estimate.
        // Mixed-resource destination planning trims unsupported/over-capacity resources later,
        // so this keeps bag fill behavior high without regressing destination safety.
        var pickupBudget = Math.Max(1, optimisticPickupBudget);
        var totalPlanned = 0;
        var plannedWeight = 0f;
        var plannedAny = storageAgent.Storage.HasOneOrMoreResources();
        var requestedByResourceId = new Dictionary<string, int>();
        var plannedAmountsByPile = new Dictionary<ResourcePileInstance, int>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in queuedPiles)
        {
            var storedResource = pile.GetStoredResource();
            if (storedResource == null || storedResource.HasDisposed)
            {
                continue;
            }

            var projected = PickupPlanningUtil.GetProjectedCapacity(storageAgent.Storage, storedResource.Blueprint, plannedWeight, plannedAny);
            if (projected <= 0)
            {
                break;
            }

            projected = Mathf.Min(projected, storedResource.Amount, pickupBudget - totalPlanned);
            if (projected <= 0)
            {
                break;
            }

            totalPlanned += projected;
            plannedWeight += PickupPlanningUtil.GetProjectedWeight(storageAgent.Storage, storedResource.Blueprint, projected);
            plannedAny = true;
            var resourceId = storedResource.Blueprint.GetID();
            requestedByResourceId[resourceId] = requestedByResourceId.TryGetValue(resourceId, out var currentAmount)
                ? currentAmount + projected
                : projected;
            plannedAmountsByPile[pile] = projected;
        }

        var plannedPiles = new List<ResourcePileInstance>(queuedPiles);
        var sameTypeAdded = 0;
        foreach (var pile in orderedCandidates)
        {
            if (!StockpileMixedSourcePlanner.TryPlanAdditionalPile(goal, queue, pile, storageAgent.Storage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
            {
                continue;
            }

            sameTypeAdded++;
            plannedPiles.Add(pile);
        }

        var mixedPlan = StockpileMixedSourcePlanner.Apply(
            goal,
            creature,
            plannedPiles,
            queue,
            firstPile,
            firstStorage,
            preferredDestinationOrder,
            storageAgent.Storage,
            pickupBudget,
            ref plannedWeight,
            ref plannedAny,
            ref totalPlanned,
            requestedByResourceId,
            plannedAmountsByPile,
            mixedGroundHarvestExtent,
            getOptimisticPickupBudget);

        var claimed = ClusterOwnershipStore.ClaimCluster(goal, creature, plannedPiles);
        ClusterOwnershipStore.RefreshGoal(goal);

        if (claimed > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Claimed source cluster for {firstPile.BlueprintId}: mode={(sameTypeSweep.UsePatchSweep ? "patch" : "route")}, claimed={claimed}, queued={queuedPiles.Count}, routed={orderedCandidates.Count}, planned={plannedPiles.Count}, sameTypeGround={sameTypeSweep.SameTypeTotal}:{sameTypeSweep.SameTypeAmount}, withinBudget={sameTypeSweep.SameTypeWithinBudget}:{sameTypeSweep.SameTypeWithinBudgetAmount}, route[sourceToTarget={(sameTypeSweep.SourceToTargetDistance >= 0f ? sameTypeSweep.SourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, budget={sameTypeSweep.DetourBudget:0.0}], rejected[claimed={sameTypeSweep.ClaimedByOther}, detour={sameTypeSweep.DetourRejected}, reach={sameTypeSweep.ReachRejected}, cooldown={sameTypeSweep.CooldownRejected}, validate={sameTypeSweep.ValidateRejected}, priority={sameTypeSweep.PriorityRejected}, store={sameTypeSweep.StorageRejected}], details=[{string.Join("; ", sameTypeSweep.DetailSamples)}], owner={goal.AgentOwner}",
                80);
        }

        var destinationOutcome = ResourceDestinationPlanCoordinator.Apply(
            goal,
            creature,
            firstPile,
            firstStorage,
            primaryCandidatePlan,
            preferredDestinationOrder,
            queue,
            plannedPiles,
            requestedByResourceId,
            plannedAmountsByPile,
            getOptimisticPickupBudget,
            clearTargetsQueue,
            queueTarget);
        totalPlanned = requestedByResourceId.Values.Sum();

        if (requestedByResourceId.Count > 1)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Planned mixed ground harvest for {firstPile.BlueprintId}: resources=[{string.Join(", ", requestedByResourceId.Select(entry => $"{entry.Key}={entry.Value}"))}]",
                120);
        }

        DiagnosticTrace.Info(
            "haul.plan",
            $"Clustered source piles for {firstPile.BlueprintId}: mode={(sameTypeSweep.UsePatchSweep ? "patch" : "route")}, sameTypeAdded={sameTypeAdded}, mixedAdded={mixedPlan.AddedCount}, totalTargeted={totalPlanned}, pickupBudget={pickupBudget}, routeBudget={sameTypeSweep.DetourBudget:0.0}, sourceToTarget={(sameTypeSweep.SourceToTargetDistance >= 0f ? sameTypeSweep.SourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, destinations={destinationOutcome.Summary}, mixed=[{string.Join("; ", mixedPlan.Details)}]",
            120);

        return new StockpileClusterAugmentResult(
            destinationOutcome,
            new Dictionary<string, int>(requestedByResourceId),
            totalPlanned);
    }

    internal static float GetDetourBudget(
        float sourceToTargetDistance,
        float sourceClusterExtent,
        float detourBudgetMultiplier,
        float minimumDetourBudget,
        float maximumDetourBudget)
    {
        return HaulGeometry.GetDetourBudget(
            sourceToTargetDistance,
            sourceClusterExtent,
            detourBudgetMultiplier,
            minimumDetourBudget,
            maximumDetourBudget);
    }

    internal static bool ShouldUsePatchSweep(
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sameTypePiles,
        int patchSweepAmountThreshold,
        int patchSweepCountThreshold)
    {
        var storedResource = firstPile.GetStoredResource();
        return storedResource != null &&
               HaulGeometry.ShouldUsePatchSweep(
                   firstPile.BlueprintId,
                   storedResource.Amount,
                   sameTypePiles.Count,
                   patchSweepAmountThreshold,
                   patchSweepCountThreshold);
    }

    internal static bool IsSweepCandidateWorthwhile(
        ResourcePileInstance firstPile,
        ResourcePileInstance candidatePile,
        Vector3? targetPosition,
        float detourBudget,
        bool usePatchSweep,
        HashSet<ResourcePileInstance> patchComponent,
        float sourceClusterExtent,
        out float detourCost)
    {
        if (usePatchSweep)
        {
            detourCost = Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition());
            return patchComponent.Contains(candidatePile);
        }

        return IsRouteWorthwhile(firstPile, candidatePile, targetPosition, detourBudget, sourceClusterExtent, out detourCost);
    }

    internal static float GetAdditionalDetour(ResourcePileInstance firstPile, ResourcePileInstance candidatePile, Vector3? targetPosition)
    {
        return targetPosition.HasValue
            ? HaulGeometry.GetAdditionalDetour(
                firstPile.GetPosition(),
                candidatePile.GetPosition(),
                targetPosition.Value)
            : Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition());
    }

    internal static Vector3? TryGetPosition(object? instance)
    {
        return PositionReflection.TryGetPosition(instance);
    }

    private static bool IsRouteWorthwhile(
        ResourcePileInstance firstPile,
        ResourcePileInstance candidatePile,
        Vector3? targetPosition,
        float detourBudget,
        float sourceClusterExtent,
        out float detourCost)
    {
        return HaulGeometry.IsRouteWorthwhile(
            firstPile.GetPosition(),
            candidatePile.GetPosition(),
            targetPosition,
            detourBudget,
            sourceClusterExtent,
            out detourCost);
    }

}
