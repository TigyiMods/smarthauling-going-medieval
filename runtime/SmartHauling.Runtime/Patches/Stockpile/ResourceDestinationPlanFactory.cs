using NSEipix;
using NSMedieval;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal sealed class PlannedResourceGroup
{
    public PlannedResourceGroup(string resourceId, IReadOnlyList<ResourcePileInstance> piles, ResourceInstance sampleResource, ZonePriority sourcePriority)
    {
        ResourceId = resourceId;
        Piles = piles;
        SampleResource = sampleResource;
        SourcePriority = sourcePriority;
    }

    public string ResourceId { get; }

    public IReadOnlyList<ResourcePileInstance> Piles { get; }

    public ResourceInstance SampleResource { get; }

    public ZonePriority SourcePriority { get; }
}

internal sealed class ResourceDestinationBuild
{
    public ResourceDestinationBuild(
        IReadOnlyList<StockpileDestinationResourcePlan> resourcePlans,
        IReadOnlyList<StorageCandidatePlanner.StorageCandidatePlan> candidatePlans,
        IReadOnlyCollection<string> unsupportedResourceIds,
        IReadOnlyDictionary<string, int> requestedAmountByResourceId)
    {
        ResourcePlans = resourcePlans;
        CandidatePlans = candidatePlans;
        UnsupportedResourceIds = unsupportedResourceIds;
        RequestedAmountByResourceId = requestedAmountByResourceId;
    }

    public IReadOnlyList<StockpileDestinationResourcePlan> ResourcePlans { get; }

    public IReadOnlyList<StorageCandidatePlanner.StorageCandidatePlan> CandidatePlans { get; }

    public IReadOnlyCollection<string> UnsupportedResourceIds { get; }

    public IReadOnlyDictionary<string, int> RequestedAmountByResourceId { get; }
}

internal static class ResourceDestinationPlanFactory
{
    public static ResourceDestinationBuild Build(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        IStorage firstStorage,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        IReadOnlyCollection<ResourcePileInstance> plannedPiles,
        IReadOnlyDictionary<string, int> requestedByResourceId,
        string primaryResourceId)
    {
        var resourceGroups = plannedPiles
            .Where(pile => pile != null && !pile.HasDisposed)
            .GroupBy(pile => pile.Blueprint.GetID())
            .ToDictionary(
                group => group.Key,
                group => new PlannedResourceGroup(
                    group.Key,
                    group.ToList(),
                    group.Select(item => item.GetStoredResource()).FirstOrDefault(resource => resource != null && !resource.HasDisposed)!,
                    (ZonePriority)group.Max(item => (int)StoragePriorityUtil.GetEffectiveSourcePriority(item))));

        var orderedResourceIds = OrderResourceIds(requestedByResourceId, primaryResourceId);
        var resourcePlans = new List<StockpileDestinationResourcePlan>();
        var candidatePlans = new List<StorageCandidatePlanner.StorageCandidatePlan>();
        var requestedByResource = new Dictionary<string, int>();
        var unsupported = new HashSet<string>();
        var localReservedByStorage = new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);

        foreach (var resourceId in orderedResourceIds)
        {
            if (!resourceGroups.TryGetValue(resourceId, out var group) ||
                group.SampleResource == null ||
                group.SampleResource.HasDisposed ||
                !requestedByResourceId.TryGetValue(resourceId, out var requestedAmount) ||
                requestedAmount <= 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            var basePlan = StorageCandidatePlanner.BuildPlan(
                goal,
                creature,
                group.SampleResource,
                ZonePriority.None,
                group.SourcePriority,
                enablePriorityFallback: false,
                requestedAmount,
                preferredStorage: resourceId == primaryResourceId ? firstStorage : null,
                preferredOrder: preferredDestinationOrder);

            var adjustedCandidates = new List<StorageCandidatePlanner.StorageCandidate>();
            foreach (var candidate in basePlan.Candidates)
            {
                var locallyReserved = localReservedByStorage.TryGetValue(candidate.Storage, out var reserved) ? reserved : 0;
                var adjustedCapacity = System.Math.Max(0, candidate.EstimatedCapacity - locallyReserved);
                if (adjustedCapacity <= 0)
                {
                    continue;
                }

                adjustedCandidates.Add(new StorageCandidatePlanner.StorageCandidate(
                    candidate.Storage,
                    adjustedCapacity,
                    candidate.Distance,
                    candidate.FitRatio,
                    candidate.PriorityOvershoot,
                    candidate.PreferredOrderRank,
                    candidate.Position,
                    candidate.LeasedAmount + locallyReserved));
            }

            if (adjustedCandidates.Count == 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            var adjustedPlan = new StorageCandidatePlanner.StorageCandidatePlan(
                adjustedCandidates,
                basePlan.SourcePriority,
                basePlan.EffectiveMinimumPriority,
                requestedAmount);
            var plannedAmount = System.Math.Max(0, adjustedPlan.GetEstimatedCapacityBudget(requestedAmount));
            if (plannedAmount <= 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            requestedByResource[resourceId] = plannedAmount;
            resourcePlans.Add(new StockpileDestinationResourcePlan(resourceId, adjustedPlan.OrderedStorages, plannedAmount));
            candidatePlans.Add(new StorageCandidatePlanner.StorageCandidatePlan(
                adjustedCandidates,
                adjustedPlan.SourcePriority,
                adjustedPlan.EffectiveMinimumPriority,
                plannedAmount));

            var remaining = plannedAmount;
            foreach (var candidate in adjustedCandidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var reservedAmount = Mathf.Min(candidate.EstimatedCapacity, remaining);
                if (reservedAmount <= 0)
                {
                    continue;
                }

                localReservedByStorage[candidate.Storage] = localReservedByStorage.TryGetValue(candidate.Storage, out var current)
                    ? current + reservedAmount
                    : reservedAmount;
                remaining -= reservedAmount;
            }
        }

        return new ResourceDestinationBuild(resourcePlans, candidatePlans, unsupported, requestedByResource);
    }

    public static IReadOnlyList<string> OrderResourceIds(
        IReadOnlyDictionary<string, int> requestedByResourceId,
        string primaryResourceId)
    {
        return requestedByResourceId.Keys
            .OrderBy(resourceId => resourceId == primaryResourceId ? 0 : 1)
            .ThenByDescending(resourceId => requestedByResourceId[resourceId])
            .ToList();
    }

    public static string Describe(ResourceDestinationBuild build, IEnumerable<string> prunedResources)
    {
        var plans = string.Join(
            "; ",
            build.ResourcePlans.Select(plan => $"{plan.ResourceId}:{plan.RequestedAmount}->{plan.OrderedStorages.Count}"));
        var pruned = string.Join(", ", prunedResources.Distinct());
        return $"plans=[{plans}], pruned=[{(string.IsNullOrWhiteSpace(pruned) ? "<none>" : pruned)}]";
    }
}
