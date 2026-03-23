using HarmonyLib;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using SmartHauling.Runtime.Goals;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class UnloadExecutionPlanner
{
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
            foreach (var storageTarget in candidate.OrderedStorages)
            {
                if (storageTarget == null || storageTarget.HasDisposed)
                {
                    continue;
                }

                if (!storageTarget.ReserveStorage(candidate.Resource, creature, out var storedAmount, out var position) ||
                    storedAmount.Amount <= 0)
                {
                    continue;
                }

                if (CoordinatedStockpileExecutionStore.HasFailedDrop(goal, candidate.Resource.BlueprintId, storageTarget, position))
                {
                    storageTarget.ReleaseReservations(creature);
                    continue;
                }

                ForceTarget(goal, TargetIndex.B, new TargetObject(storageTarget, position));
                CoordinatedStockpileExecutionStore.SetActiveDrop(
                    goal,
                    new CoordinatedDropReservation(
                        candidate.Resource.BlueprintId,
                        candidate.Resource.Blueprint,
                        storedAmount.Amount,
                        storageTarget,
                        position));

                DiagnosticTrace.Info(
                    GetLogCategory(goal),
                    $"Prepared drop for {goal.AgentOwner}: resource={candidate.Resource.BlueprintId}, amount={storedAmount.Amount}, storage={storageTarget.GetType().Name}[prio={storageTarget.Priority}]@{position}, source={candidate.StorageSource}",
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
        var success = false;
        var storedAmount = 0;

        if (target.ObjectInstance is StockpileInstance stockpileInstance)
        {
            var existingPile = stockpileInstance.GetResourcePileGridPosition(activeDrop.Position);
            if (existingPile != null && existingPile.Blueprint == activeDrop.Blueprint)
            {
                storedAmount = storage.TransferTo(existingPile.GetStorage(), activeDrop.Blueprint, amountToStore);
                success = storedAmount > 0;
            }
            else
            {
                var stackingLimit = activeDrop.Blueprint?.StackingLimit ?? 0;
                if (stackingLimit <= 0)
                {
                    return false;
                }

                var spawnAmount = System.Math.Min(amountToStore, stackingLimit);
                var pileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(
                    carriedResource.Clone(spawnAmount),
                    GridUtils.GetWorldPosition(activeDrop.Position));
                if (pileView != null)
                {
                    MonoSingleton<ResourcePileTracker>.Instance.OnNewPileSpawnedOnStockpile(activeDrop.Blueprint, pileView.ResourcePileInstance);
                    storage.Consume(activeDrop.Blueprint, spawnAmount);
                    storedAmount = spawnAmount;
                    success = true;
                }
            }
        }
        else if (target.ObjectInstance is ShelfComponentInstance shelfComponentInstance)
        {
            var remaining = amountToStore;
            foreach (var shelfStorage in shelfComponentInstance.AllStorage)
            {
                var justStored = shelfStorage.StoreResourcePile(creature, activeDrop.Blueprint, remaining);
                remaining -= justStored;
                storedAmount += justStored;
                if (remaining <= 0)
                {
                    break;
                }
            }

            success = storedAmount > 0;
        }

        CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

        if (!success)
        {
            CoordinatedStockpileExecutionStore.MarkFailedDrop(goal, activeDrop);
            DiagnosticTrace.Info(
                GetLogCategory(goal),
                $"Marked failed drop target for {goal.AgentOwner}: resource={activeDrop.ResourceId}, storage={target.ObjectInstance?.GetType().Name ?? "<none>"}@{activeDrop.Position}",
                120);
            return false;
        }

        SyncPickedCount(goal, storage);
        CoordinatedStockpileExecutionStore.ResetDropFailures(goal);
        DiagnosticTrace.Info(
            GetLogCategory(goal),
            $"Stored {activeDrop.ResourceId}:{storedAmount} for {goal.AgentOwner}, carryBefore={carryBefore}, carryAfter={storage.GetTotalStoredCount()}, carriedBefore=[{carriedBeforeSummary}], carriedAfter=[{CarrySummaryUtil.Summarize(storage)}]",
            120);
        return true;
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
        var resourcePriorityRanks = BuildResourcePriorityRanks(goal);
        var candidates = new List<UnloadRouteCandidate>();

        foreach (var carriedResource in carried)
        {
            if (!TryResolveOrderedStorages(goal, creature, carriedResource, sourcePriority, preferPlannedStorages, out var orderedStorages, out var sourceLabel, out var nearestDistance))
            {
                continue;
            }

            var priorityRank = resourcePriorityRanks.TryGetValue(carriedResource.BlueprintId, out var rank)
                ? rank
                : int.MaxValue;
            candidates.Add(new UnloadRouteCandidate(
                carriedResource,
                orderedStorages,
                sourceLabel,
                nearestDistance,
                priorityRank));
        }

        return candidates
            .OrderBy(candidate => candidate.NearestDistance)
            .ThenBy(candidate => candidate.PriorityRank)
            .ThenByDescending(candidate => candidate.Resource.Amount)
            .ToList();
    }

    private static bool TryResolveOrderedStorages(
        Goal goal,
        CreatureBase creature,
        ResourceInstance resource,
        ZonePriority sourcePriority,
        bool preferPlannedStorages,
        out IReadOnlyList<IStorage> orderedStorages,
        out string sourceLabel,
        out float nearestDistance)
    {
        if (preferPlannedStorages &&
            CoordinatedDropPlanLookup.TryGetPlannedStorages(goal, resource.BlueprintId, out var plannedStorages) &&
            TryOrderStoragesByDistance(creature, plannedStorages, out orderedStorages, out nearestDistance))
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
            orderedStorages = null!;
            sourceLabel = "none";
            nearestDistance = float.MaxValue;
            return false;
        }

        var orderedCandidates = candidatePlan.Candidates
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.PreferredOrderRank)
            .ToList();
        orderedStorages = orderedCandidates
            .Select(candidate => candidate.Storage)
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
        if (orderedStorages.Count == 0)
        {
            sourceLabel = "none";
            nearestDistance = float.MaxValue;
            return false;
        }

        nearestDistance = orderedCandidates.FirstOrDefault()?.Distance ?? float.MaxValue;
        sourceLabel = "planner";
        return true;
    }

    private static bool TryOrderStoragesByDistance(
        CreatureBase creature,
        IReadOnlyList<IStorage> storages,
        out IReadOnlyList<IStorage> orderedStorages,
        out float nearestDistance)
    {
        var ordered = storages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Select((storage, index) => new
            {
                Storage = storage,
                Index = index,
                Distance = GetDistanceToStorage(creature, storage)
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Storage)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();

        orderedStorages = ordered;
        nearestDistance = ordered.Count == 0 ? float.MaxValue : GetDistanceToStorage(creature, ordered[0]);
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

    private static Dictionary<string, int> BuildResourcePriorityRanks(Goal goal)
    {
        var ranks = new Dictionary<string, int>();
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
        {
            for (var index = 0; index < task.DropOrder.Count; index++)
            {
                ranks[task.DropOrder[index]] = index;
            }

            return ranks;
        }

        if (!StockpileDestinationPlanStore.TryGet(goal, out var destinationPlan))
        {
            return ranks;
        }

        ranks[destinationPlan.PrimaryResourceId] = 0;
        var nextRank = 1;
        foreach (var resourcePlan in destinationPlan.ResourcePlans)
        {
            if (!ranks.ContainsKey(resourcePlan.ResourceId))
            {
                ranks[resourcePlan.ResourceId] = nextRank++;
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
            IReadOnlyList<IStorage> orderedStorages,
            string storageSource,
            float nearestDistance,
            int priorityRank)
        {
            Resource = resource;
            OrderedStorages = orderedStorages;
            StorageSource = storageSource;
            NearestDistance = nearestDistance;
            PriorityRank = priorityRank;
        }

        public ResourceInstance Resource { get; }

        public IReadOnlyList<IStorage> OrderedStorages { get; }

        public string StorageSource { get; }

        public float NearestDistance { get; }

        public int PriorityRank { get; }
    }
}
