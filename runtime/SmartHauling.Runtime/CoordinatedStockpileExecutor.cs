using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NSEipix;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using NSMedieval.StorageUniversal;
using NSMedieval.Views.Resources;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class CoordinatedStockpileExecutor
{
    private const int MaxDropRetries = 3;
    private const float RemainingCapacityFillExtent = 16f;
    private const int MaxFillCandidatesToInspect = 24;

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> MaxCarryAmountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("MaxCaryAmount");

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> PickedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("PickedCount");

    private static readonly System.Reflection.MethodInfo ForceTargetMethod =
        AccessTools.Method(typeof(Goal), "ForceTarget", new[] { typeof(TargetIndex), typeof(TargetObject) })!;

    private static readonly System.Reflection.MethodInfo JumpToActionMethod =
        AccessTools.Method(typeof(Goal), "JumpToAction", new[] { typeof(GoapAction) })!;

    public static IEnumerable<GoapAction> Build(StockpileHaulingGoal goal)
    {
        var decide = GeneralActions.Instant("CoordinatedHaul.Decide");
        var dropInvalidPickup = GeneralActions.Instant("CoordinatedHaul.DropInvalidPickup");
        var goToPickup = GoToActions.GoToTargetNoFailCheck(TargetIndex.A, PathCompleteMode.ExactPosition)
            .JumpIfTargetDisposedForbiddenOrNull(dropInvalidPickup, TargetIndex.A);
        var pickup = ResourceActions.PickupResourceFromPile(
                TargetIndex.A,
                blueprint => GetPickupRequestAmount(goal, blueprint))
            .JumpIfTargetDisposedForbiddenOrNull(dropInvalidPickup, TargetIndex.A);
        var prepareDrop = GeneralActions.Instant("CoordinatedHaul.PrepareDrop");
        var goToDrop = GoToActions.GoToTarget(TargetIndex.B, PathCompleteMode.ExactPosition)
            .SkipIfTargetDisposedForbidenOrNull(TargetIndex.B)
            .FailAtCondition(() => goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent || storageAgent.Storage.IsEmpty());
        var storeDrop = GeneralActions.Instant("CoordinatedHaul.Store");
        var done = GeneralActions.Instant("CoordinatedHaul.Done");

        decide.OnInit = delegate
        {
            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
            {
                goal.EndGoalWith(GoalCondition.Error);
                return;
            }

            CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

            var carry = storageAgent.Storage.GetTotalStoredCount();
            var pickupQueue = goal.GetTargetQueue(TargetIndex.A);
            var pickupQueueCount = pickupQueue.Count;
            var pickupBudget = GetPickupBudget(goal, storageAgent.Storage);
            var nextPickupPile = pickupQueueCount > 0 ? pickupQueue[0].GetObjectAs<ResourcePileInstance>() : null;
            var nextPickup = nextPickupPile?.BlueprintId ?? "<none>";
            var canTakeNextPickup = CanTakeAdditionalPickup(storageAgent.Storage, nextPickupPile);

            if (carry <= 0)
            {
                CoordinatedStockpileExecutionStore.ResetDropPhase(goal);
            }

            if (carry > 0 && CoordinatedStockpileExecutionStore.IsDropPhaseLocked(goal))
            {
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Decide for {goal.AgentOwner}: carry={carry}, free={storageAgent.Storage.GetFreeSpace():0.##}, pickupQueue={pickupQueueCount}, pickupBudget={pickupBudget}, nextPickup={nextPickup}, choice=prepareDropLocked",
                    200);
                JumpToAction(goal, prepareDrop);
                return;
            }

            if (carry > 0 && (pickupQueueCount == 0 || !canTakeNextPickup) &&
                TryAppendRemainingCapacityPickup(goal, creature, storageAgent.Storage, pickupQueueCount > 0))
            {
                pickupQueue = goal.GetTargetQueue(TargetIndex.A);
                pickupQueueCount = pickupQueue.Count;
                nextPickupPile = pickupQueueCount > 0 ? pickupQueue[0].GetObjectAs<ResourcePileInstance>() : null;
                nextPickup = nextPickupPile?.BlueprintId ?? "<none>";
                canTakeNextPickup = CanTakeAdditionalPickup(storageAgent.Storage, nextPickupPile);
            }

            var decision = "prepareDrop";

            if (carry <= 0)
            {
                if (pickupQueueCount > 0)
                {
                    decision = "selectPickup";
                    DiagnosticTrace.Info(
                        "coord.exec",
                        $"Decide for {goal.AgentOwner}: carry={carry}, pickupQueue={pickupQueueCount}, pickupBudget={pickupBudget}, nextPickup={nextPickup}, choice={decision}",
                        200);
                    if (!TrySelectCurrentPickupTarget(goal))
                    {
                        JumpToAction(goal, dropInvalidPickup);
                        return;
                    }

                    JumpToAction(goal, goToPickup);
                    return;
                }

                decision = "done";
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Decide for {goal.AgentOwner}: carry={carry}, pickupQueue={pickupQueueCount}, pickupBudget={pickupBudget}, nextPickup={nextPickup}, choice={decision}",
                    200);
                JumpToAction(goal, done);
                return;
            }

            if (pickupQueueCount > 0 && canTakeNextPickup)
            {
                decision = "selectPickup";
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Decide for {goal.AgentOwner}: carry={carry}, free={storageAgent.Storage.GetFreeSpace():0.##}, pickupQueue={pickupQueueCount}, pickupBudget={pickupBudget}, nextPickup={nextPickup}, choice={decision}",
                    200);
                if (!TrySelectCurrentPickupTarget(goal))
                {
                    JumpToAction(goal, dropInvalidPickup);
                    return;
                }

                JumpToAction(goal, goToPickup);
                return;
            }

            DiagnosticTrace.Info(
                "coord.exec",
                $"Decide for {goal.AgentOwner}: carry={carry}, free={storageAgent.Storage.GetFreeSpace():0.##}, pickupQueue={pickupQueueCount}, pickupBudget={pickupBudget}, nextPickup={nextPickup}, choice={decision}",
                200);
            JumpToAction(goal, prepareDrop);
        };

        dropInvalidPickup.OnInit = delegate
        {
            var invalidReason = DescribeInvalidPickupReason(goal);
            var invalidPile = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
            DiagnosticTrace.Info(
                "coord.exec",
                $"Dropping invalid pickup for {goal.AgentOwner}: current={invalidPile?.BlueprintId ?? "<none>"}, queue={goal.GetTargetQueue(TargetIndex.A).Count}, reason={invalidReason}",
                120);
            if (invalidPile != null && invalidReason == "validate")
            {
                HaulFailureBackoffStore.MarkFailed(new[] { invalidPile });
                StockpileTaskBoard.MarkFailed(invalidPile);
            }
            DropCurrentPickupTarget(goal);
            JumpToAction(goal, decide);
        };

        pickup.OnComplete = status =>
        {
            if (status == ActionCompletionStatus.Success)
            {
                RememberCurrentPickupAnchor(goal);
                CompleteCurrentPickupTarget(goal);
                if (goal.AgentOwner is IStorageAgent { Storage: not null } storageAgent)
                {
                    DiagnosticTrace.Info(
                        "coord.exec",
                        $"Pickup complete for {goal.AgentOwner}: carry={storageAgent.Storage.GetTotalStoredCount()}, pickupQueueRemaining={goal.GetTargetQueue(TargetIndex.A).Count}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
                        200);
                }
                JumpToAction(goal, decide);
            }
        };

        prepareDrop.OnInit = delegate
        {
            if (!TryPrepareNextDrop(goal))
            {
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Failed to prepare drop for {goal.AgentOwner}, carried=[{CarrySummaryUtil.Summarize((goal.AgentOwner as IStorageAgent)?.Storage)}]",
                    80);
                goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            CoordinatedStockpileExecutionStore.MarkDropPhaseStarted(goal);
            JumpToAction(goal, goToDrop);
        };

        goToDrop.OnComplete = status =>
        {
            if (status == ActionCompletionStatus.Success)
            {
                return;
            }

            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
                storageAgent.Storage.IsEmpty())
            {
                return;
            }

            var failures = CoordinatedStockpileExecutionStore.IncrementDropFailures(goal);
            if (CoordinatedStockpileExecutionStore.TryGet(goal, out var state) && state.ActiveDrop != null)
            {
                CoordinatedStockpileExecutionStore.MarkFailedDrop(goal, state.ActiveDrop);
            }
            DiagnosticTrace.Info(
                "coord.exec",
                $"Drop move failed for {goal.AgentOwner}: status={status}, failures={failures}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
                120);
            CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

            if (failures < MaxDropRetries)
            {
                JumpToAction(goal, prepareDrop);
            }
        };

        storeDrop.OnInit = delegate
        {
            if (!TryStoreActiveDrop(goal))
            {
                var failures = CoordinatedStockpileExecutionStore.IncrementDropFailures(goal);
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Failed to store active drop for {goal.AgentOwner}, failures={failures}, carried=[{CarrySummaryUtil.Summarize((goal.AgentOwner as IStorageAgent)?.Storage)}]",
                    80);

                if (failures < MaxDropRetries)
                {
                    JumpToAction(goal, prepareDrop);
                    return;
                }

                goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            CoordinatedStockpileExecutionStore.ResetDropFailures(goal);
            JumpToAction(goal, decide);
        };

        yield return decide;
        yield return goToPickup;
        yield return pickup;
        yield return dropInvalidPickup;
        yield return prepareDrop;
        yield return goToDrop;
        yield return storeDrop;
        yield return done;
    }

    private static int GetPickupRequestAmount(StockpileHaulingGoal goal, Resource blueprint)
    {
        if (goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return 0;
        }

        var projectedCapacity = PickupPlanningUtil.GetProjectedCapacity(
            storageAgent.Storage,
            blueprint,
            0f,
            storageAgent.Storage.HasOneOrMoreResources());
        if (projectedCapacity <= 0)
        {
            return 0;
        }

        return projectedCapacity;
    }

    private static int GetPickupBudget(HaulingBaseGoal goal, Storage storage)
    {
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task) && task.PickupBudget > 0)
        {
            return task.PickupBudget;
        }

        var planned = MaxCarryAmountRef(goal);
        return planned > 0 ? planned : System.Math.Max(1, storage.GetTotalStoredCount());
    }

    private static bool TrySelectCurrentPickupTarget(Goal goal)
    {
        var queue = goal.GetTargetQueue(TargetIndex.A);
        if (queue.Count == 0)
        {
            return false;
        }

        var nextTarget = queue[0];
        var pile = nextTarget.GetObjectAs<ResourcePileInstance>();
        if (pile == null || pile.HasDisposed)
        {
            return false;
        }

        ForceTarget(goal, TargetIndex.A, nextTarget);
        return true;
    }

    private static bool CanTakeAdditionalPickup(Storage storage, ResourcePileInstance? nextPickupPile)
    {
        if (storage == null || nextPickupPile == null || nextPickupPile.HasDisposed)
        {
            return false;
        }

        var resource = nextPickupPile.GetStoredResource();
        if (resource == null || resource.HasDisposed)
        {
            return false;
        }

        return PickupPlanningUtil.GetProjectedCapacity(
            storage,
            resource.Blueprint,
            0f,
            storage.HasOneOrMoreResources()) > 0;
    }

    private static bool TryAppendRemainingCapacityPickup(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        Storage storage,
        bool prependToFront)
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
                !CanReachPile(goal, pile) ||
                !ValidatePile(goal, pile))
            {
                continue;
            }

            var storedResource = pile.GetStoredResource();
            if (storedResource == null || storedResource.HasDisposed || storedResource.Amount <= 0)
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

            if (!TryResolveFillDestinationPlan(goal, creature, storedResource, pile, requestedAmount, out var orderedStorages))
            {
                continue;
            }

            var candidate = new DynamicFillCandidate(
                pile,
                storedResource.Blueprint,
                requestedAmount,
                orderedStorages,
                Vector3.Distance(creature.GetPosition(), pile.GetPosition()),
                patchDistance,
                TryGetPlannedStorages(goal, storedResource.BlueprintId, out _));
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
        if (!MonoSingleton<ReservationManager>.Instance.TryReserveObject(bestCandidate.Pile, goal.AgentOwner))
        {
            MonoSingleton<ReservationManager>.Instance.ReleaseAll(bestCandidate.Pile);
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
            bestCandidate.RequestedAmount);
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

    private static bool TryResolveFillDestinationPlan(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourceInstance storedResource,
        ResourcePileInstance pile,
        int requestedAmount,
        out IReadOnlyList<IStorage> orderedStorages)
    {
        if (TryGetPlannedStorages(goal, storedResource.BlueprintId, out orderedStorages) && orderedStorages.Count > 0)
        {
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
            return false;
        }

        orderedStorages = candidatePlan.OrderedStorages;
        return orderedStorages.Count > 0;
    }

    private static bool TryPrepareNextDrop(StockpileHaulingGoal goal)
    {
        if (goal.AgentOwner is not CreatureBase creature ||
            goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return false;
        }

        CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);

        var carriedResources = BuildOrderedCarriedResources(goal, storageAgent.Storage);
        if (carriedResources.Count == 0)
        {
            return false;
        }

        var sourcePriority = ZonePriority.None;
        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
        {
            sourcePriority = task.SourcePriority;
        }
        else
        {
            HaulingPriorityRules.TryGetGoalSourcePriority(goal, creature, out sourcePriority);
        }

        foreach (var carriedResource in carriedResources)
        {
            if (!TryGetPlannedStorages(goal, carriedResource.BlueprintId, out var orderedStorages))
            {
                continue;
            }

            foreach (var storage in orderedStorages)
            {
                if (storage == null || storage.HasDisposed)
                {
                    continue;
                }

                if (!storage.ReserveStorage(carriedResource, creature, out var storedAmount, out var position) ||
                    storedAmount.Amount <= 0)
                {
                    continue;
                }

                if (CoordinatedStockpileExecutionStore.HasFailedDrop(goal, carriedResource.BlueprintId, storage, position))
                {
                    storage.ReleaseReservations(creature);
                    continue;
                }

                ForceTarget(goal, TargetIndex.B, new TargetObject(storage, position));
                CoordinatedStockpileExecutionStore.SetActiveDrop(
                    goal,
                    new CoordinatedDropReservation(
                        carriedResource.BlueprintId,
                        carriedResource.Blueprint,
                        storedAmount.Amount,
                        storage,
                        position));

                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Prepared drop for {goal.AgentOwner}: resource={carriedResource.BlueprintId}, amount={storedAmount.Amount}, storage={storage.GetType().Name}[prio={storage.Priority}]@{position}, source=task",
                    120);
                return true;
            }
        }

        return false;
    }

    private static bool TryStoreActiveDrop(StockpileHaulingGoal goal)
    {
        if (goal.AgentOwner is not CreatureBase creature ||
            goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
            !CoordinatedStockpileExecutionStore.TryGet(goal, out var state) ||
            state.ActiveDrop == null)
        {
            return false;
        }

        var activeDrop = state.ActiveDrop;
        var storage = storageAgent.Storage;
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
                "coord.exec",
                $"Marked failed drop target for {goal.AgentOwner}: resource={activeDrop.ResourceId}, storage={target.ObjectInstance?.GetType().Name ?? "<none>"}@{activeDrop.Position}",
                120);
            return false;
        }

        SyncPickedCount(goal, storage);
        CoordinatedStockpileExecutionStore.ResetDropFailures(goal);
        DiagnosticTrace.Info(
            "coord.exec",
            $"Stored {activeDrop.ResourceId}:{storedAmount} for {goal.AgentOwner}, carryBefore={carryBefore}, carryAfter={storage.GetTotalStoredCount()}, carriedBefore=[{carriedBeforeSummary}], carriedAfter=[{CarrySummaryUtil.Summarize(storage)}]",
            120);
        return true;
    }

    private static List<ResourceInstance> BuildOrderedCarriedResources(Goal goal, Storage storage)
    {
        var carried = CarrySummaryUtil.Snapshot(storage);
        if (carried.Count <= 1)
        {
            return carried;
        }

        if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
        {
            var priorityByResourceId = new Dictionary<string, int>();
            for (var i = 0; i < task.DropOrder.Count; i++)
            {
                priorityByResourceId[task.DropOrder[i]] = i;
            }

            return carried
                .OrderBy(resource => priorityByResourceId.TryGetValue(resource.BlueprintId, out var rank) ? rank : int.MaxValue)
                .ThenByDescending(resource => resource.Amount)
                .ToList();
        }

        if (!StockpileDestinationPlanStore.TryGet(goal, out var destinationPlan))
        {
            return carried
                .OrderByDescending(resource => resource.Amount)
                .ToList();
        }

        var legacyPriorityByResourceId = new Dictionary<string, int>
        {
            [destinationPlan.PrimaryResourceId] = 0
        };
        var nextPriority = 1;
        foreach (var resourcePlan in destinationPlan.ResourcePlans)
        {
            if (!legacyPriorityByResourceId.ContainsKey(resourcePlan.ResourceId))
            {
                legacyPriorityByResourceId[resourcePlan.ResourceId] = nextPriority++;
            }
        }

        return carried
            .OrderBy(resource => legacyPriorityByResourceId.TryGetValue(resource.BlueprintId, out var rank) ? rank : int.MaxValue)
            .ThenByDescending(resource => resource.Amount)
            .ToList();
    }

    private static void ForceTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        ForceTargetMethod.Invoke(goal, new object[] { index, target });
    }

    private static void DropCurrentPickupTarget(Goal goal)
    {
        var currentTarget = goal.GetTarget(TargetIndex.A);
        if (!currentTarget.IsInitialized)
        {
            return;
        }

        RemoveMatchingQueuedPickup(goal, currentTarget);
        ReleasePickupReservation(goal, currentTarget);
    }

    private static void CompleteCurrentPickupTarget(Goal goal)
    {
        var currentTarget = goal.GetTarget(TargetIndex.A);
        if (!currentTarget.IsInitialized)
        {
            return;
        }

        RemoveMatchingQueuedPickup(goal, currentTarget);
        ReleasePickupReservation(goal, currentTarget);
    }

    private static void RemoveMatchingQueuedPickup(Goal goal, TargetObject currentTarget)
    {
        var queue = goal.GetTargetQueue(TargetIndex.A);
        var currentPile = currentTarget.GetObjectAs<ResourcePileInstance>();
        if (currentPile == null)
        {
            return;
        }

        var index = queue.FindIndex(target => ReferenceEquals(target.GetObjectAs<ResourcePileInstance>(), currentPile));
        if (index >= 0)
        {
            queue.RemoveAt(index);
        }
    }

    private static void ReleasePickupReservation(Goal goal, TargetObject currentTarget)
    {
        if (goal.AgentOwner == null)
        {
            return;
        }

        var reservable = currentTarget.GetAsReservable();
        if (reservable != null)
        {
            MonoSingleton<ReservationManager>.Instance.ReleaseObject(reservable, goal.AgentOwner);
        }
    }

    private static void JumpToAction(Goal goal, GoapAction action)
    {
        JumpToActionMethod.Invoke(goal, new object[] { action });
    }

    private static void SyncPickedCount(HaulingBaseGoal goal, Storage storage)
    {
        PickedCountRef(goal) = storage?.GetTotalStoredCount() ?? 0;
    }

    private static bool TryGetPlannedStorages(Goal goal, string resourceId, out IReadOnlyList<IStorage> storages)
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

    private static void RememberCurrentPickupAnchor(StockpileHaulingGoal goal)
    {
        var pile = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
        if (pile == null || pile.HasDisposed)
        {
            return;
        }

        CoordinatedStockpileExecutionStore.RememberPickupPosition(goal, pile.GetPosition());
    }

    private static string DescribeInvalidPickupReason(StockpileHaulingGoal goal)
    {
        return HaulSourcePolicy.DescribeInvalidPickupReason(
            goal,
            goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>());
    }

    private static bool CanReachPile(StockpileHaulingGoal goal, ResourcePileInstance pile)
    {
        return HaulSourcePolicy.CanReachPile(goal, pile);
    }

    private static bool ValidatePile(HaulingBaseGoal goal, ResourcePileInstance pile)
    {
        return HaulSourcePolicy.ValidatePile(goal, pile);
    }

    private sealed class DynamicFillCandidate
    {
        public DynamicFillCandidate(
            ResourcePileInstance pile,
            Resource blueprint,
            int requestedAmount,
            IReadOnlyList<IStorage> orderedStorages,
            float distance,
            float patchDistance,
            bool hasExistingDropPlan)
        {
            Pile = pile;
            Blueprint = blueprint;
            RequestedAmount = requestedAmount;
            OrderedStorages = orderedStorages;
            Distance = distance;
            PatchDistance = patchDistance;
            HasExistingDropPlan = hasExistingDropPlan;
        }

        public ResourcePileInstance Pile { get; }

        public Resource Blueprint { get; }

        public int RequestedAmount { get; }

        public IReadOnlyList<IStorage> OrderedStorages { get; }

        public float Distance { get; }

        public float PatchDistance { get; }

        public bool HasExistingDropPlan { get; }

        public float Score
        {
            get => LocalFillPlanner.CalculateCandidateScore(
                RequestedAmount,
                Distance,
                PatchDistance,
                HasExistingDropPlan);
        }
    }
}
