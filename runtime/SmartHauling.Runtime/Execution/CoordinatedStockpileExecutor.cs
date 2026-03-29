using HarmonyLib;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime;

/// <summary>
/// Builds and drives the custom stockpile hauling execution state machine.
/// </summary>
/// <remarks>
/// The executor consumes centrally planned tasks and performs pickup, local refill, drop, and
/// cleanup in explicit phases. It should stay focused on execution, not high-level task selection.
/// </remarks>
internal static class CoordinatedStockpileExecutor
{
    private const int MaxDropRetries = 3;
    private const int MaxConsecutiveInvalidPickupRecoveriesBeforeDrop = 3;

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> MaxCarryAmountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("MaxCaryAmount");

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> PickedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("PickedCount");

    private static readonly System.Reflection.MethodInfo ForceTargetMethod =
        AccessTools.Method(typeof(Goal), "ForceTarget", new[] { typeof(TargetIndex), typeof(TargetObject) })!;

    private static readonly System.Reflection.MethodInfo JumpToActionMethod =
        AccessTools.Method(typeof(Goal), "JumpToAction", new[] { typeof(GoapAction) })!;

    /// <summary>
    /// Builds the custom action sequence injected into stockpile hauling goals.
    /// </summary>
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
                CoordinatedStockpileExecutionStore.ResetInvalidPickupRecoveries(goal);
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

            if (carry > 0 &&
                (pickupQueueCount == 0 || !canTakeNextPickup) &&
                RemainingCapacityFillPlanner.TryAppend(goal, creature, storageAgent.Storage, pickupQueueCount > 0))
            {
                pickupQueue = goal.GetTargetQueue(TargetIndex.A);
                pickupQueueCount = pickupQueue.Count;
                nextPickupPile = pickupQueueCount > 0 ? pickupQueue[0].GetObjectAs<ResourcePileInstance>() : null;
                nextPickup = nextPickupPile?.BlueprintId ?? "<none>";
                canTakeNextPickup = CanTakeAdditionalPickup(storageAgent.Storage, nextPickupPile);
            }

            if (carry > 0 &&
                pickupQueueCount > 0 &&
                !canTakeNextPickup &&
                TryPromoteCompatiblePickupTarget(goal, storageAgent.Storage))
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
            var carry = (goal.AgentOwner as IStorageAgent)?.Storage?.GetTotalStoredCount() ?? 0;
            var invalidAttempts = CoordinatedStockpileExecutionStore.IncrementInvalidPickupRecoveries(goal);
            var invalidReason = HaulSourcePolicy.DescribeInvalidPickupReason(
                goal,
                goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>());
            var invalidPile = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
            DiagnosticTrace.Info(
                "coord.exec",
                $"Dropping invalid pickup for {goal.AgentOwner}: current={invalidPile?.BlueprintId ?? "<none>"}, queue={goal.GetTargetQueue(TargetIndex.A).Count}, reason={invalidReason}, attempts={invalidAttempts}, carry={carry}",
                120);
            if (invalidPile != null && invalidReason == "validate")
            {
                HaulFailureBackoffStore.MarkFailed(new[] { invalidPile });
                StockpileTaskBoard.MarkFailed(invalidPile);
            }
            var didRecoverTarget = TryDropInvalidPickupTarget(goal);
            if (goal is StockpileHaulingGoal stockpileGoal &&
                goal.AgentOwner is CreatureBase creature &&
                goal.AgentOwner is IStorageAgent { Storage: not null } storageAgent)
            {
                var removedQueued = PruneAndDeduplicatePickupQueue(stockpileGoal, storageAgent.Storage);
                if (removedQueued > 0)
                {
                    DiagnosticTrace.Info(
                        "coord.exec",
                        $"Pruned pickup queue after invalid target for {goal.AgentOwner}: removed={removedQueued}, queue={goal.GetTargetQueue(TargetIndex.A).Count}, reason={invalidReason}",
                        120);
                }

                if (invalidReason == "disposed" &&
                    TryReplanDisposedPickup(stockpileGoal, creature, storageAgent.Storage, out var replannedQueue, out var appended))
                {
                    CoordinatedStockpileExecutionStore.ResetInvalidPickupRecoveries(goal);
                    DiagnosticTrace.Info(
                        "coord.exec",
                        $"Replanned after disposed pickup for {goal.AgentOwner}: queue={replannedQueue}, appended={appended}, carry={carry}",
                        120);
                    JumpToAction(goal, decide);
                    return;
                }
            }

            if (!didRecoverTarget)
            {
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Unable to recover invalid pickup for {goal.AgentOwner}: queue={goal.GetTargetQueue(TargetIndex.A).Count}, reason={invalidReason}",
                    80);
                goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            if (carry > 0 && invalidAttempts >= MaxConsecutiveInvalidPickupRecoveriesBeforeDrop)
            {
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Forced drop phase for {goal.AgentOwner} after repeated invalid pickups: attempts={invalidAttempts}, carry={carry}",
                    120);
                JumpToAction(goal, prepareDrop);
                return;
            }

            JumpToAction(goal, decide);
        };

        pickup.OnComplete = status =>
        {
            if (status == ActionCompletionStatus.Success)
            {
                CoordinatedStockpileExecutionStore.ResetInvalidPickupRecoveries(goal);
                RemainingCapacityFillPlanner.RememberCurrentPickupAnchor(goal);
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
            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
                !UnloadExecutionPlanner.TryPrepareNextDrop(goal, creature, storageAgent.Storage, preferPlannedStorages: true))
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
            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
                !UnloadExecutionPlanner.TryStoreActiveDrop(goal, creature, storageAgent.Storage))
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

        done.OnInit = delegate
        {
            DiagnosticTrace.Info(
                "coord.exec",
                $"Completing hauling goal for {goal.AgentOwner}: carry=0, pickupQueue={goal.GetTargetQueue(TargetIndex.A).Count}",
                120);
            goal.EndGoalWith(GoalCondition.Succeeded);
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

        if (goal is not StockpileHaulingGoal stockpileGoal ||
            !HaulSourcePolicy.ValidatePile(stockpileGoal, pile) ||
            !HaulSourcePolicy.CanReachPile(stockpileGoal, pile))
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

    private static bool TryPromoteCompatiblePickupTarget(Goal goal, Storage storage)
    {
        var queue = goal.GetTargetQueue(TargetIndex.A);
        for (var index = 0; index < queue.Count; index++)
        {
            var target = queue[index];
            var pile = target.GetObjectAs<ResourcePileInstance>();
            if (pile == null || pile.HasDisposed)
            {
                queue.RemoveAt(index);
                ReleasePickupReservation(goal, target);
                index--;
                continue;
            }

            if (!CanTakeAdditionalPickup(storage, pile))
            {
                continue;
            }

            if (index > 0)
            {
                queue.RemoveAt(index);
                queue.Insert(0, target);
                DiagnosticTrace.Info(
                    "coord.exec",
                    $"Promoted compatible pickup for {goal.AgentOwner}: resource={pile.BlueprintId}, fromIndex={index}, queue={queue.Count}",
                    120);
            }

            return true;
        }

        return false;
    }

    private static void ForceTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        ForceTargetMethod.Invoke(goal, new object[] { index, target });
    }

    private static bool TryDropInvalidPickupTarget(Goal goal)
    {
        var currentTarget = goal.GetTarget(TargetIndex.A);
        var queue = goal.GetTargetQueue(TargetIndex.A);
        var queuedPiles = queue
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .ToList();
        var recoveryPlan = InvalidPickupRecovery.CreatePlan(
            currentTarget.IsInitialized,
            currentTarget.GetObjectAs<ResourcePileInstance>(),
            queuedPiles,
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        if (!recoveryPlan.HasAnyAction)
        {
            return false;
        }

        TargetObject? removedTarget = null;
        if (recoveryPlan.HasQueueTarget && recoveryPlan.QueueIndexToDrop < queue.Count)
        {
            removedTarget = queue[recoveryPlan.QueueIndexToDrop];
            queue.RemoveAt(recoveryPlan.QueueIndexToDrop);
            ReleasePickupReservation(goal, removedTarget.Value);
        }

        if (recoveryPlan.ReleaseCurrentTarget && currentTarget.IsInitialized)
        {
            var currentInstance = currentTarget.ObjectInstance;
            var removedInstance = removedTarget?.ObjectInstance;
            if (!ReferenceEquals(currentInstance, removedInstance))
            {
                ReleasePickupReservation(goal, currentTarget);
            }
        }

        return true;
    }

    private static int PruneAndDeduplicatePickupQueue(StockpileHaulingGoal goal, Storage storage)
    {
        if (goal == null)
        {
            return 0;
        }

        var removed = 0;
        var queue = goal.GetTargetQueue(TargetIndex.A);
        var seen = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        for (var index = queue.Count - 1; index >= 0; index--)
        {
            var target = queue[index];
            var pile = target.GetObjectAs<ResourcePileInstance>();
            var isInvalid = pile == null ||
                            pile.HasDisposed ||
                            !seen.Add(pile) ||
                            !HaulSourcePolicy.ValidatePile(goal, pile) ||
                            !HaulSourcePolicy.CanReachPile(goal, pile);
            if (!isInvalid)
            {
                continue;
            }

            queue.RemoveAt(index);
            ReleasePickupReservation(goal, target);
            removed++;
        }

        return removed;
    }

    private static bool TryReplanDisposedPickup(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        Storage storage,
        out int queueCount,
        out int appended)
    {
        queueCount = goal.GetTargetQueue(TargetIndex.A).Count;
        appended = 0;
        if (storage == null || storage.GetFreeSpace() <= 0f)
        {
            return queueCount > 0;
        }

        var attempts = 0;
        while (attempts < 4 && storage.GetFreeSpace() > 0f)
        {
            attempts++;
            var beforeCount = goal.GetTargetQueue(TargetIndex.A).Count;
            if (!RemainingCapacityFillPlanner.TryAppend(goal, creature, storage, beforeCount > 0))
            {
                break;
            }

            var afterCount = goal.GetTargetQueue(TargetIndex.A).Count;
            if (afterCount <= beforeCount)
            {
                break;
            }

            appended += afterCount - beforeCount;
            PruneAndDeduplicatePickupQueue(goal, storage);
        }

        queueCount = goal.GetTargetQueue(TargetIndex.A).Count;
        return queueCount > 0;
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
            RuntimeServices.Reservations.ReleaseObject(reservable, goal.AgentOwner);
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

}
