using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
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
        var candidatePiles = new List<ResourcePileInstance>();
        var sameTypeTotal = 0;
        var sameTypeAmount = 0;
        var sameTypeWithinBudget = 0;
        var sameTypeWithinBudgetAmount = 0;
        var claimedByOther = 0;
        var validateRejected = 0;
        var priorityRejected = 0;
        var storageRejected = 0;
        var detourRejected = 0;
        var reachRejected = 0;
        var cooldownRejected = 0;
        var detailSamples = new List<string>();
        var targetPosition = TryGetPosition(firstStorage);
        var sourceToTargetDistance = targetPosition.HasValue
            ? Vector3.Distance(firstPile.GetPosition(), targetPosition.Value)
            : -1f;
        var detourBudget = GetDetourBudget(
            sourceToTargetDistance,
            sourceClusterExtent,
            detourBudgetMultiplier,
            minimumDetourBudget,
            maximumDetourBudget);
        var sameTypePiles = sourcePatchPiles
            .Where(pile => pile != null && !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();
        var usePatchSweep = ShouldUsePatchSweep(
            firstPile,
            sameTypePiles,
            patchSweepAmountThreshold,
            patchSweepCountThreshold);
        var patchComponent = usePatchSweep
            ? BuildPatchComponent(firstPile, sameTypePiles, patchSweepExtent, patchSweepLinkExtent)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in sameTypePiles)
        {
            if (knownPiles.Contains(pile) ||
                pile.HasDisposed ||
                pile.Blueprint != firstPile.Blueprint)
            {
                continue;
            }

            var storedResource = pile.GetStoredResource();
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeTotal++;
                sameTypeAmount += storedResource.Amount;
            }

            if (!IsSweepCandidateWorthwhile(
                    firstPile,
                    pile,
                    targetPosition,
                    detourBudget,
                    usePatchSweep,
                    patchComponent,
                    sourceClusterExtent,
                    out var detourCost))
            {
                detourRejected++;
                CaptureDetail(
                    detailSamples,
                    usePatchSweep
                        ? $"{pile.BlueprintId}:patch({detourCost:0.0}>{patchSweepExtent:0.0})"
                        : targetPosition.HasValue
                            ? $"{pile.BlueprintId}:detour({detourCost:0.0}>{detourBudget:0.0})"
                            : $"{pile.BlueprintId}:radius");
                continue;
            }

            sameTypeWithinBudget++;
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeWithinBudgetAmount += storedResource.Amount;
            }

            if (!ClusterOwnershipStore.CanUsePile(creature, pile))
            {
                claimedByOther++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:claimed");
                continue;
            }

            if (HaulFailureBackoffStore.IsCoolingDown(pile))
            {
                cooldownRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:cooldown");
                continue;
            }

            if (!HaulSourcePolicy.CanReachPile(goal, pile))
            {
                reachRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:reach");
                continue;
            }

            if (!HaulSourcePolicy.ValidatePile(goal, pile))
            {
                validateRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:validate");
                continue;
            }

            if (storedResource == null ||
                storedResource.HasDisposed)
            {
                continue;
            }

            var pileSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
            if (!HaulingPriorityRules.CanMoveToPriority(pileSourcePriority, firstStorage.Priority))
            {
                priorityRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:priority({pileSourcePriority}->{firstStorage.Priority})");
                continue;
            }

            if (!firstStorage.CanStore(storedResource, creature))
            {
                storageRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:store");
                continue;
            }

            candidatePiles.Add(pile);
        }

        var orderedCandidates = candidatePiles
            .OrderBy(pile => usePatchSweep
                ? Vector3.Distance(firstPile.GetPosition(), pile.GetPosition())
                : GetAdditionalDetour(firstPile, pile, targetPosition))
            .ThenBy(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()))
            .ToList();

        var optimisticPickupBudget = getOptimisticPickupBudget(goal, firstPile.Blueprint);
        var pickupBudget = Math.Max(1, destinationCapacityBudget > 0 ? Mathf.Min(destinationCapacityBudget, optimisticPickupBudget) : optimisticPickupBudget);
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
            if (!TryPlanAdditionalPile(goal, queue, pile, storageAgent.Storage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
            {
                continue;
            }

            sameTypeAdded++;
            plannedPiles.Add(pile);
        }

        var mixedAdded = 0;
        var mixedDetails = new List<string>();
        if (totalPlanned < pickupBudget)
        {
            var plannedPileSet = new HashSet<ResourcePileInstance>(plannedPiles, ReferenceEqualityComparer<ResourcePileInstance>.Instance);
            var mixedCandidates = RuntimeServices.WorldSnapshot.GetAllKnownPileInstances()
                .Where(pile =>
                    !plannedPileSet.Contains(pile) &&
                    !pile.HasDisposed &&
                    IsNearPlannedSourcePatch(plannedPiles, pile, mixedGroundHarvestExtent))
                .ToList();

            foreach (var pile in mixedCandidates
                         .OrderBy(candidate => GetNearestPatchDistance(plannedPiles, candidate))
                         .ThenBy(candidate => Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition())))
            {
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

                if (!TryPlanAdditionalPile(goal, queue, pile, storageAgent.Storage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
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
        }

        var claimed = ClusterOwnershipStore.ClaimCluster(goal, creature, plannedPiles);
        ClusterOwnershipStore.RefreshGoal(goal);

        if (claimed > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Claimed source cluster for {firstPile.BlueprintId}: mode={(usePatchSweep ? "patch" : "route")}, claimed={claimed}, queued={queuedPiles.Count}, routed={orderedCandidates.Count}, planned={plannedPiles.Count}, sameTypeGround={sameTypeTotal}:{sameTypeAmount}, withinBudget={sameTypeWithinBudget}:{sameTypeWithinBudgetAmount}, route[sourceToTarget={(sourceToTargetDistance >= 0f ? sourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, budget={detourBudget:0.0}], rejected[claimed={claimedByOther}, detour={detourRejected}, reach={reachRejected}, cooldown={cooldownRejected}, validate={validateRejected}, priority={priorityRejected}, store={storageRejected}], details=[{string.Join("; ", detailSamples)}], owner={goal.AgentOwner}",
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
            $"Clustered source piles for {firstPile.BlueprintId}: mode={(usePatchSweep ? "patch" : "route")}, sameTypeAdded={sameTypeAdded}, mixedAdded={mixedAdded}, totalTargeted={totalPlanned}, pickupBudget={pickupBudget}, routeBudget={detourBudget:0.0}, sourceToTarget={(sourceToTargetDistance >= 0f ? sourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, destinations={destinationOutcome.Summary}, mixed=[{string.Join("; ", mixedDetails)}]",
            120);

        return new StockpileClusterAugmentResult(
            destinationOutcome,
            new Dictionary<string, int>(requestedByResourceId),
            totalPlanned);
    }

    internal static float GetNearestPatchDistance(IReadOnlyCollection<ResourcePileInstance> plannedPiles, ResourcePileInstance candidatePile)
    {
        return HaulGeometry.GetNearestPatchDistance(
            plannedPiles.Select(plannedPile => plannedPile.GetPosition()),
            candidatePile.GetPosition());
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

    internal static HashSet<ResourcePileInstance> BuildPatchComponent(
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sameTypePiles,
        float patchSweepExtent,
        float patchSweepLinkExtent)
    {
        var component = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var frontier = new Queue<ResourcePileInstance>();
        component.Add(firstPile);
        frontier.Enqueue(firstPile);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var candidate in sameTypePiles)
            {
                if (component.Contains(candidate) ||
                    candidate.HasDisposed ||
                    Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition()) > patchSweepExtent ||
                    Vector3.Distance(current.GetPosition(), candidate.GetPosition()) > patchSweepLinkExtent)
                {
                    continue;
                }

                component.Add(candidate);
                frontier.Enqueue(candidate);
            }
        }

        return component;
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
        if (instance == null)
        {
            return null;
        }

        var method = HarmonyLib.AccessTools.Method(instance.GetType(), "GetPosition", System.Type.EmptyTypes);
        if (method == null)
        {
            return null;
        }

        var result = method.Invoke(instance, null);
        return result switch
        {
            Vector3 vector => vector,
            _ => null
        };
    }

    private static bool TryPlanAdditionalPile(
        Goal goal,
        List<TargetObject> queue,
        ResourcePileInstance pile,
        NSMedieval.Components.Storage storage,
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
        return GetNearestPatchDistance(plannedPiles, candidatePile) <= mixedGroundHarvestExtent;
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

    private static void CaptureDetail(List<string> details, string value)
    {
        if (details.Count < 8)
        {
            details.Add(value);
        }
    }
}
