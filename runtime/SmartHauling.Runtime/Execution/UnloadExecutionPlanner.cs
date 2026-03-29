using HarmonyLib;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using SmartHauling.Runtime.Goals;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class UnloadExecutionPlanner
{
    private const int MaxSameStorageBurstDrops = 8;

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> PickedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("PickedCount");

    private static readonly System.Reflection.MethodInfo ForceTargetMethod =
        AccessTools.Method(typeof(Goal), "ForceTarget", new[] { typeof(TargetIndex), typeof(TargetObject) })!;

    public static bool HasAnyUnloadDestination(Goal goal, CreatureBase creature, Storage storage, bool preferPlannedStorages)
    {
        return BuildOrderedCarriedResources(goal, creature, storage, preferPlannedStorages).Count > 0;
    }

    public static bool TryPrepareNextDrop(Goal goal, CreatureBase creature, Storage storage, bool preferPlannedStorages)
    {
        CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

        foreach (var candidate in BuildOrderedCarriedResources(goal, creature, storage, preferPlannedStorages))
        {
            foreach (var storageTarget in candidate.OrderedTargets)
            {
                if (storageTarget.Storage == null || storageTarget.Storage.HasDisposed)
                {
                    continue;
                }

                var requestedAmount = Math.Min(candidate.Resource.Amount, storageTarget.RequestedAmount);
                if (requestedAmount <= 0)
                {
                    continue;
                }

                var requestedResource = requestedAmount >= candidate.Resource.Amount
                    ? candidate.Resource
                    : candidate.Resource.Clone(requestedAmount);
                if (!storageTarget.Storage.ReserveStorage(requestedResource, creature, out var storedAmount, out var position) ||
                    storedAmount.Amount <= 0)
                {
                    continue;
                }

                if (CoordinatedStockpileExecutionStore.HasFailedDrop(goal, candidate.Resource.BlueprintId, storageTarget.Storage, position))
                {
                    storageTarget.Storage.ReleaseReservations(creature);
                    continue;
                }

                ForceTarget(goal, TargetIndex.B, new TargetObject(storageTarget.Storage, position));
                CoordinatedStockpileExecutionStore.SetActiveDrop(
                    goal,
                    new CoordinatedDropReservation(
                        candidate.Resource.BlueprintId,
                        candidate.Resource.Blueprint,
                        storedAmount.Amount,
                        storageTarget.Storage,
                        position));

                DiagnosticTrace.Info(
                    GetLogCategory(goal),
                    $"Prepared drop for {goal.AgentOwner}: resource={candidate.Resource.BlueprintId}, amount={storedAmount.Amount}, storage={storageTarget.Storage.GetType().Name}[prio={storageTarget.Storage.Priority}]@{position}, source={candidate.StorageSource}",
                    120);
                return true;
            }
        }

        return false;
    }

    public static bool TryStoreActiveDrop(Goal goal, CreatureBase creature, Storage storage)
    {
        if (!CoordinatedStockpileExecutionStore.TryGet(goal, out var state) ||
            state.ActiveDrop == null)
        {
            return false;
        }

        var activeDrop = state.ActiveDrop;
        var carriedResource = CarrySummaryUtil.Snapshot(storage)
            .FirstOrDefault(resource => resource.BlueprintId == activeDrop.ResourceId);
        if (carriedResource == null || carriedResource.HasDisposed || carriedResource.Amount <= 0)
        {
            CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);
            SyncPickedCount(goal, storage);
            return true;
        }

        var carryBefore = storage.GetTotalStoredCount();
        var carriedBeforeSummary = CarrySummaryUtil.Summarize(storage);
        var amountToStore = System.Math.Min(carriedResource.Amount, System.Math.Max(1, activeDrop.ReservedAmount));
        var target = goal.GetTarget(TargetIndex.B);
        if (!TryStoreReservation(target.ObjectInstance, creature, storage, carriedResource, activeDrop.Blueprint, activeDrop.Position, amountToStore, out var storedAmount))
        {
            CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);
            CoordinatedStockpileExecutionStore.MarkFailedDrop(goal, activeDrop);
            DiagnosticTrace.Info(
                GetLogCategory(goal),
                $"Marked failed drop target for {goal.AgentOwner}: resource={activeDrop.ResourceId}, storage={target.ObjectInstance?.GetType().Name ?? "<none>"}@{activeDrop.Position}",
                120);
            return false;
        }

        storedAmount += TryStoreAdditionalSameStorageDrops(
            goal,
            target.ObjectInstance,
            activeDrop.Storage,
            creature,
            storage,
            activeDrop.ResourceId,
            activeDrop.Blueprint);
        CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

        SyncPickedCount(goal, storage);
        CoordinatedStockpileExecutionStore.ResetDropFailures(goal);
        DiagnosticTrace.Info(
            GetLogCategory(goal),
            $"Stored {activeDrop.ResourceId}:{storedAmount} for {goal.AgentOwner}, carryBefore={carryBefore}, carryAfter={storage.GetTotalStoredCount()}, carriedBefore=[{carriedBeforeSummary}], carriedAfter=[{CarrySummaryUtil.Summarize(storage)}]",
            120);
        return true;
    }

    private static int TryStoreAdditionalSameStorageDrops(
        Goal goal,
        object? targetObject,
        IStorage targetStorage,
        CreatureBase creature,
        Storage carryStorage,
        string resourceId,
        Resource blueprint)
    {
        var totalStored = 0;
        for (var iteration = 0; iteration < MaxSameStorageBurstDrops; iteration++)
        {
            var carriedResource = CarrySummaryUtil.Snapshot(carryStorage)
                .FirstOrDefault(resource => resource.BlueprintId == resourceId);
            if (carriedResource == null || carriedResource.HasDisposed || carriedResource.Amount <= 0)
            {
                break;
            }

            if (!targetStorage.ReserveStorage(carriedResource, creature, out var storedAmount, out var position) ||
                storedAmount.Amount <= 0)
            {
                break;
            }

            if (!TryStoreReservation(
                    targetObject,
                    creature,
                    carryStorage,
                    carriedResource,
                    blueprint,
                    position,
                    storedAmount.Amount,
                    out var justStored))
            {
                targetStorage.ReleaseReservations(creature);
                break;
            }

            totalStored += justStored;
            if (justStored < storedAmount.Amount)
            {
                break;
            }
        }

        totalStored += TryStoreAdditionalPlannedResourcesAtCurrentStorage(
            goal,
            targetObject,
            targetStorage,
            creature,
            carryStorage,
            resourceId,
            blueprint);

        return totalStored;
    }

    private static int TryStoreAdditionalPlannedResourcesAtCurrentStorage(
        Goal goal,
        object? targetObject,
        IStorage targetStorage,
        CreatureBase creature,
        Storage carryStorage,
        string activeResourceId,
        Resource activeBlueprint)
    {
        var totalStored = 0;
        const int maxIterations = 16;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var candidate = CarrySummaryUtil.Snapshot(carryStorage)
                .FirstOrDefault(resource =>
                    resource != null &&
                    !resource.HasDisposed &&
                    resource.Amount > 0 &&
                    resource.Blueprint != null &&
                    !(resource.BlueprintId == activeResourceId && resource.Blueprint == activeBlueprint) &&
                    IsPlannedStorageForResource(goal, resource.BlueprintId, targetStorage));
            if (candidate == null || candidate.Blueprint == null)
            {
                break;
            }

            if (!targetStorage.ReserveStorage(candidate, creature, out var storedAmount, out var position) ||
                storedAmount.Amount <= 0)
            {
                break;
            }

            if (!TryStoreReservation(
                    targetObject,
                    creature,
                    carryStorage,
                    candidate,
                    candidate.Blueprint,
                    position,
                    storedAmount.Amount,
                    out var justStored))
            {
                targetStorage.ReleaseReservations(creature);
                break;
            }

            totalStored += justStored;
            if (justStored < storedAmount.Amount)
            {
                break;
            }
        }

        if (totalStored > 0)
        {
            DiagnosticTrace.Info(
                GetLogCategory(goal),
                $"Stored additional co-located resources at current storage for {goal.AgentOwner}: amount={totalStored}, storage={targetStorage.GetType().Name}[prio={targetStorage.Priority}]",
                120);
        }

        return totalStored;
    }

    private static bool IsPlannedStorageForResource(Goal goal, string resourceId, IStorage storage)
    {
        if (goal == null || storage == null || storage.HasDisposed || string.IsNullOrWhiteSpace(resourceId))
        {
            return false;
        }

        if (CoordinatedDropPlanLookup.TryGetPlannedAllocations(goal, resourceId, out var allocations))
        {
            if (allocations.Any(allocation => allocation?.Storage != null && ReferenceEquals(allocation.Storage, storage)))
            {
                return true;
            }
        }

        if (CoordinatedDropPlanLookup.TryGetPlannedStorages(goal, resourceId, out var plannedStorages))
        {
            if (plannedStorages.Any(planned => planned != null && ReferenceEquals(planned, storage)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStoreReservation(
        object? targetObject,
        CreatureBase creature,
        Storage carryStorage,
        ResourceInstance carriedResource,
        Resource blueprint,
        Vec3Int position,
        int amountToStore,
        out int storedAmount)
    {
        storedAmount = 0;
        if (amountToStore <= 0)
        {
            return false;
        }

        if (targetObject is StockpileInstance stockpileInstance)
        {
            var existingPile = stockpileInstance.GetResourcePileGridPosition(position);
            if (existingPile != null && existingPile.Blueprint == blueprint)
            {
                storedAmount = carryStorage.TransferTo(existingPile.GetStorage(), blueprint, amountToStore);
                return storedAmount > 0;
            }

            var stackingLimit = blueprint?.StackingLimit ?? 0;
            if (stackingLimit <= 0)
            {
                return false;
            }

            var spawnAmount = System.Math.Min(amountToStore, stackingLimit);
            var pileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(
                carriedResource.Clone(spawnAmount),
                GridUtils.GetWorldPosition(position));
            if (pileView == null)
            {
                return false;
            }

            MonoSingleton<ResourcePileTracker>.Instance.OnNewPileSpawnedOnStockpile(blueprint, pileView.ResourcePileInstance);
            carryStorage.Consume(blueprint, spawnAmount);
            storedAmount = spawnAmount;
            return true;
        }

        if (targetObject is ShelfComponentInstance shelfComponentInstance)
        {
            var remaining = amountToStore;
            foreach (var shelfStorage in shelfComponentInstance.AllStorage)
            {
                var justStored = shelfStorage.StoreResourcePile(creature, blueprint, remaining);
                remaining -= justStored;
                storedAmount += justStored;
                if (remaining <= 0)
                {
                    break;
                }
            }

            return storedAmount > 0;
        }

        return false;
    }

    private static List<UnloadRouteCandidate> BuildOrderedCarriedResources(
        Goal goal,
        CreatureBase creature,
        Storage storage,
        bool preferPlannedStorages)
    {
        var carried = CarrySummaryUtil.Snapshot(storage);
        if (carried.Count == 0)
        {
            return new List<UnloadRouteCandidate>();
        }

        var sourcePriority = ZonePriority.None;
        HaulingPriorityRules.TryGetGoalSourcePriority(goal, creature, out sourcePriority);
        var resourcePriorityRanks = BuildResourcePriorityBands(goal);
        var candidates = new List<UnloadRouteCandidate>();

        foreach (var carriedResource in carried)
        {
            if (!TryResolveOrderedTargets(
                    goal,
                    creature,
                    carriedResource,
                    sourcePriority,
                    preferPlannedStorages,
                    out var orderedTargets,
                    out var sourceLabel,
                    out var nearestDistance))
            {
                continue;
            }

            var priorityRank = resourcePriorityRanks.TryGetValue(carriedResource.BlueprintId, out var rank)
                ? rank
                : int.MaxValue;
            var targetPriority = orderedTargets.FirstOrDefault()?.Storage?.Priority ?? ZonePriority.None;
            var anchorPosition = orderedTargets
                .Select(target => StorageCandidatePlanner.TryGetPosition(target.Storage))
                .FirstOrDefault(position => position.HasValue);
            candidates.Add(new UnloadRouteCandidate(
                carriedResource,
                orderedTargets,
                sourceLabel,
                nearestDistance,
                priorityRank,
                targetPriority,
                anchorPosition));
        }

        return UnloadRouteOrdering.OrderCandidates(
            candidates,
            creature.GetPosition(),
            candidate => candidate.PriorityRank,
            candidate => candidate.TargetPriority,
            candidate => candidate.AnchorPosition,
            candidate => candidate.NearestDistance,
            candidate => candidate.Resource.Amount)
            .ToList();
    }

    private static bool TryResolveOrderedTargets(
        Goal goal,
        CreatureBase creature,
        ResourceInstance resource,
        ZonePriority sourcePriority,
        bool preferPlannedStorages,
        out IReadOnlyList<UnloadRouteTarget> orderedTargets,
        out string sourceLabel,
        out float nearestDistance)
    {
        if (preferPlannedStorages &&
            CoordinatedDropPlanLookup.TryGetPlannedAllocations(goal, resource.BlueprintId, out var storedAllocations) &&
            TryUsePlannedAllocations(storedAllocations, creature, out orderedTargets, out nearestDistance))
        {
            sourceLabel = "plan";
            return true;
        }

        if (preferPlannedStorages &&
            CoordinatedDropPlanLookup.TryGetPlannedStorages(goal, resource.BlueprintId, out var plannedStorages) &&
            TryUsePlannedStorages(plannedStorages, creature, out orderedTargets, out nearestDistance))
        {
            sourceLabel = "plan";
            return true;
        }

        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            resource,
            ZonePriority.None,
            sourcePriority,
            enablePriorityFallback: false,
            resource.Amount);
        if (candidatePlan.Primary == null)
        {
            orderedTargets = null!;
            sourceLabel = "none";
            nearestDistance = float.MaxValue;
            return false;
        }

        var orderedCandidates = candidatePlan.Candidates.ToList();
        var plannedAmount = candidatePlan.GetEstimatedCapacityBudget(resource.Amount);
        var plannedAllocations = StorageAllocationPlanBuilder.BuildFromCandidates(orderedCandidates, plannedAmount);
        orderedTargets = plannedAllocations.Count > 0
            ? plannedAllocations
                .Select(allocation => new UnloadRouteTarget(allocation.Storage, allocation.RequestedAmount))
                .ToList()
            : orderedCandidates
                .Select(candidate => candidate.Storage)
                .Where(storage => storage != null && !storage.HasDisposed)
                .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
                .Select(storage => new UnloadRouteTarget(storage, int.MaxValue))
                .ToList();
        if (orderedTargets.Count == 0)
        {
            sourceLabel = "none";
            nearestDistance = float.MaxValue;
            return false;
        }

        nearestDistance = orderedCandidates.FirstOrDefault()?.Distance ?? float.MaxValue;
        sourceLabel = "planner";
        return true;
    }

    private static bool TryUsePlannedAllocations(
        IReadOnlyList<StockpileStorageAllocation> allocations,
        CreatureBase creature,
        out IReadOnlyList<UnloadRouteTarget> orderedTargets,
        out float nearestDistance)
    {
        var ordered = StorageAllocationPlanBuilder.MergeAllocations(allocations)
            .Select(allocation => new UnloadRouteTarget(allocation.Storage, allocation.RequestedAmount))
            .ToList();

        orderedTargets = ordered;
        nearestDistance = ordered.Count == 0
            ? float.MaxValue
            : ordered.Min(target => GetDistanceToStorage(creature, target.Storage));
        return ordered.Count > 0;
    }

    private static bool TryUsePlannedStorages(
        IReadOnlyList<IStorage> storages,
        CreatureBase creature,
        out IReadOnlyList<UnloadRouteTarget> orderedTargets,
        out float nearestDistance)
    {
        var ordered = storages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .Select(storage => new UnloadRouteTarget(storage, int.MaxValue))
            .ToList();

        orderedTargets = ordered;
        nearestDistance = ordered.Count == 0
            ? float.MaxValue
            : ordered.Min(target => GetDistanceToStorage(creature, target.Storage));
        return ordered.Count > 0;
    }

    private static float GetDistanceToStorage(CreatureBase creature, IStorage storage)
    {
        var position = StorageCandidatePlanner.TryGetPosition(storage) ??
                       StockpileClusterAugmentor.TryGetPosition(storage);
        return position.HasValue
            ? Vector3.Distance(creature.GetPosition(), position.Value)
            : float.MaxValue / 4f;
    }

    private static Dictionary<string, int> BuildResourcePriorityBands(Goal goal)
    {
        var ranks = new Dictionary<string, int>();
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
        {
            for (var index = 0; index < task.DropOrder.Count; index++)
            {
                var resourceId = task.DropOrder[index];
                if (!string.IsNullOrWhiteSpace(resourceId))
                {
                    ranks[resourceId] = index;
                }
            }

            return ranks;
        }

        if (!StockpileDestinationPlanStore.TryGet(goal, out var destinationPlan))
        {
            return ranks;
        }

        ranks[destinationPlan.PrimaryResourceId] = 0;
        var rank = 1;
        foreach (var resourcePlan in destinationPlan.ResourcePlans)
        {
            if (!ranks.ContainsKey(resourcePlan.ResourceId))
            {
                ranks[resourcePlan.ResourceId] = rank++;
            }
        }

        return ranks;
    }

    private static void ForceTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        ForceTargetMethod.Invoke(goal, new object[] { index, target });
    }

    private static void SyncPickedCount(Goal goal, Storage storage)
    {
        if (goal is HaulingBaseGoal haulingGoal)
        {
            PickedCountRef(haulingGoal) = storage?.GetTotalStoredCount() ?? 0;
        }
    }

    private static string GetLogCategory(Goal goal)
    {
        return goal is SmartUnloadGoal ? "unload.exec" : "coord.exec";
    }

    private sealed class UnloadRouteCandidate
    {
        public UnloadRouteCandidate(
            ResourceInstance resource,
            IReadOnlyList<UnloadRouteTarget> orderedTargets,
            string storageSource,
            float nearestDistance,
            int priorityRank,
            ZonePriority targetPriority,
            Vector3? anchorPosition)
        {
            Resource = resource;
            OrderedTargets = orderedTargets;
            StorageSource = storageSource;
            NearestDistance = nearestDistance;
            PriorityRank = priorityRank;
            TargetPriority = targetPriority;
            AnchorPosition = anchorPosition;
        }

        public ResourceInstance Resource { get; }

        public IReadOnlyList<UnloadRouteTarget> OrderedTargets { get; }

        public string StorageSource { get; }

        public float NearestDistance { get; }

        public int PriorityRank { get; }

        public ZonePriority TargetPriority { get; }

        public Vector3? AnchorPosition { get; }
    }

    private sealed class UnloadRouteTarget
    {
        public UnloadRouteTarget(IStorage storage, int requestedAmount)
        {
            Storage = storage;
            RequestedAmount = requestedAmount;
        }

        public IStorage Storage { get; }

        public int RequestedAmount { get; }
    }
}
