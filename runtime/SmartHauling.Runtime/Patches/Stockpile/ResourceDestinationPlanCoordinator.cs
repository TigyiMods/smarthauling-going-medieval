using NSEipix;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal readonly struct DestinationPlanOutcome
{
    private DestinationPlanOutcome(bool success, int leasedAmount, int storageCount, string summary)
    {
        Success = success;
        LeasedAmount = leasedAmount;
        StorageCount = storageCount;
        Summary = summary;
    }

    public bool Success { get; }

    public int LeasedAmount { get; }

    public int StorageCount { get; }

    public string Summary { get; }

    public static DestinationPlanOutcome Succeeded(int leasedAmount, int storageCount, string summary)
    {
        return new DestinationPlanOutcome(true, leasedAmount, storageCount, summary);
    }

    public static DestinationPlanOutcome Failed(string summary)
    {
        return new DestinationPlanOutcome(false, 0, 0, summary);
    }
}

internal static class ResourceDestinationPlanCoordinator
{
    public static DestinationPlanOutcome Apply(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance firstPile,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        Dictionary<string, int> requestedByResourceId,
        Dictionary<ResourcePileInstance, int> plannedAmountsByPile,
        System.Func<StockpileHaulingGoal, Resource, int> getOptimisticPickupBudget,
        System.Action<Goal, TargetIndex> clearTargetsQueue,
        System.Action<Goal, TargetIndex, TargetObject> queueTarget)
    {
        var primaryResourceId = firstPile.Blueprint.GetID();
        var prunedResources = new HashSet<string>();

        for (var pass = 0; pass < 3; pass++)
        {
            var build = ResourceDestinationPlanFactory.Build(
                goal,
                creature,
                firstStorage,
                preferredDestinationOrder,
                plannedPiles,
                requestedByResourceId,
                primaryResourceId);

            if (!build.UnsupportedResourceIds.Contains(primaryResourceId) &&
                build.ResourcePlans.Count > 0)
            {
                if (build.UnsupportedResourceIds.Count > 0)
                {
                    foreach (var unsupportedResourceId in build.UnsupportedResourceIds)
                    {
                        prunedResources.Add(unsupportedResourceId);
                    }

                    TrimPlannedPilesToRequestedAmounts(
                        goal,
                        queue,
                        plannedPiles,
                        requestedByResourceId,
                        plannedAmountsByPile,
                        build.RequestedAmountByResourceId);
                    continue;
                }

                CommitDestinationPlans(goal, build, primaryResourceId, clearTargetsQueue, queueTarget);
                var leasedAmount = DestinationLeaseStore.LeasePlans(goal, creature, build.CandidatePlans);
                return DestinationPlanOutcome.Succeeded(
                    leasedAmount,
                    build.ResourcePlans.SelectMany(plan => plan.OrderedStorages).Distinct(ReferenceEqualityComparer<IStorage>.Instance).Count(),
                    ResourceDestinationPlanFactory.Describe(build, prunedResources));
            }

            break;
        }

        if (TryBuildPrimaryOnlyDestinationPlan(
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
                queueTarget,
                out var primaryOnlyOutcome))
        {
            return primaryOnlyOutcome;
        }

        return DestinationPlanOutcome.Failed(
            $"primary={primaryResourceId}, requested=[{string.Join(", ", requestedByResourceId.Select(entry => $"{entry.Key}={entry.Value}"))}]");
    }

    private static void CommitDestinationPlans(
        StockpileHaulingGoal goal,
        ResourceDestinationBuild build,
        string primaryResourceId,
        System.Action<Goal, TargetIndex> clearTargetsQueue,
        System.Action<Goal, TargetIndex, TargetObject> queueTarget)
    {
        StockpileDestinationPlanStore.Set(goal, primaryResourceId, build.ResourcePlans);

        if (build.ResourcePlans.FirstOrDefault(plan => plan.ResourceId == primaryResourceId)?.OrderedStorages.FirstOrDefault() is { } primaryStorage)
        {
            clearTargetsQueue(goal, TargetIndex.B);
            queueTarget(goal, TargetIndex.B, new TargetObject(primaryStorage));
        }
    }

    private static void TrimPlannedPilesToRequestedAmounts(
        Goal goal,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        IDictionary<string, int> requestedByResourceId,
        IReadOnlyDictionary<ResourcePileInstance, int> plannedAmountsByPile,
        IReadOnlyDictionary<string, int> allowedRequestedAmounts)
    {
        var remainingByResource = new Dictionary<string, int>(allowedRequestedAmounts);
        var keptPiles = new List<ResourcePileInstance>();
        var removedPiles = new List<ResourcePileInstance>();

        foreach (var pile in plannedPiles)
        {
            var resourceId = pile.Blueprint.GetID();
            if (!remainingByResource.TryGetValue(resourceId, out var remaining) || remaining <= 0)
            {
                removedPiles.Add(pile);
                continue;
            }

            keptPiles.Add(pile);
            var plannedAmount = plannedAmountsByPile.TryGetValue(pile, out var amount) ? amount : (pile.GetStoredResource()?.Amount ?? 0);
            remainingByResource[resourceId] = Mathf.Max(0, remaining - plannedAmount);
        }

        foreach (var removedPile in removedPiles)
        {
            RuntimeServices.Reservations.ReleaseAll(removedPile);
        }

        queue.RemoveAll(target =>
        {
            var pile = target.GetObjectAs<ResourcePileInstance>();
            return pile != null && removedPiles.Contains(pile);
        });

        plannedPiles.Clear();
        plannedPiles.AddRange(keptPiles);

        requestedByResourceId.Clear();
        foreach (var entry in allowedRequestedAmounts)
        {
            if (entry.Value > 0)
            {
                requestedByResourceId[entry.Key] = entry.Value;
            }
        }
    }

    private static bool TryBuildPrimaryOnlyDestinationPlan(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance firstPile,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        IDictionary<string, int> requestedByResourceId,
        IReadOnlyDictionary<ResourcePileInstance, int> plannedAmountsByPile,
        System.Func<StockpileHaulingGoal, Resource, int> getOptimisticPickupBudget,
        System.Action<Goal, TargetIndex> clearTargetsQueue,
        System.Action<Goal, TargetIndex, TargetObject> queueTarget,
        out DestinationPlanOutcome outcome)
    {
        var primaryResourceId = firstPile.Blueprint.GetID();
        if (!requestedByResourceId.TryGetValue(primaryResourceId, out var requestedAmount) || requestedAmount <= 0)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-missing:{primaryResourceId}");
            return false;
        }

        var primaryResource = firstPile.GetStoredResource();
        if (primaryResource == null || primaryResource.HasDisposed)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-empty:{primaryResourceId}");
            return false;
        }

        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(firstPile);
        var reusableCandidates = primaryCandidatePlan.Candidates
            .Where(candidate =>
                candidate.Storage != null &&
                !candidate.Storage.HasDisposed &&
                candidate.EstimatedCapacity > 0 &&
                candidate.Storage.CanStore(primaryResource, creature) &&
                HaulingPriorityRules.CanMoveToPriority(sourcePriority, candidate.Storage.Priority))
            .ToList();

        var candidatePlan = reusableCandidates.Count > 0
            ? new StorageCandidatePlanner.StorageCandidatePlan(
                reusableCandidates,
                primaryCandidatePlan.SourcePriority,
                primaryCandidatePlan.EffectiveMinimumPriority,
                requestedAmount)
            : StorageCandidatePlanner.BuildPlan(
                goal,
                creature,
                primaryResource,
                ZonePriority.None,
                sourcePriority,
                enablePriorityFallback: false,
                requestedAmount,
                preferredStorage: firstStorage,
                preferredOrder: preferredDestinationOrder);

        if (candidatePlan.Primary == null)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-no-storage:{primaryResourceId}");
            return false;
        }

        var plannedAmount = System.Math.Max(0, candidatePlan.GetEstimatedCapacityBudget(requestedAmount));
        if (plannedAmount <= 0)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-no-capacity:{primaryResourceId}");
            return false;
        }

        var requestedByPrimary = new Dictionary<string, int> { [primaryResourceId] = plannedAmount };
        TrimPlannedPilesToRequestedAmounts(goal, queue, plannedPiles, requestedByResourceId, plannedAmountsByPile, requestedByPrimary);

        var resourcePlans = new[]
        {
            new StockpileDestinationResourcePlan(primaryResourceId, candidatePlan.OrderedStorages, plannedAmount)
        };
        StockpileDestinationPlanStore.Set(goal, primaryResourceId, resourcePlans);
        clearTargetsQueue(goal, TargetIndex.B);
        queueTarget(goal, TargetIndex.B, new TargetObject(candidatePlan.Primary.Storage));
        var leasedAmount = DestinationLeaseStore.LeasePlan(goal, creature, candidatePlan.Candidates, plannedAmount);
        outcome = DestinationPlanOutcome.Succeeded(
            leasedAmount,
            candidatePlan.OrderedStorages.Count,
            $"fallback=primary-only:{primaryResourceId}:{plannedAmount}");
        return true;
    }
}
