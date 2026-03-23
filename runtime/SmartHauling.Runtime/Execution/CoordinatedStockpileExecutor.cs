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
                RemainingCapacityFillPlanner.TryAppend(goal, creature, storageAgent.Storage, pickupQueueCount > 0))
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
            var invalidReason = HaulSourcePolicy.DescribeInvalidPickupReason(
                goal,
                goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>());
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
