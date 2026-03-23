using HarmonyLib;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using SmartHauling.Runtime.Goals;

namespace SmartHauling.Runtime;

internal static class SmartUnloadExecutor
{
    private const int MaxDropRetries = 3;

    private static readonly System.Reflection.MethodInfo JumpToActionMethod =
        AccessTools.Method(typeof(Goal), "JumpToAction", new[] { typeof(GoapAction) })!;

    public static IEnumerable<GoapAction> Build(SmartUnloadGoal goal)
    {
        var decide = GeneralActions.Instant("SmartUnload.Decide");
        var prepareDrop = GeneralActions.Instant("SmartUnload.PrepareDrop");
        var goToDrop = GoToActions.GoToTarget(TargetIndex.B, PathCompleteMode.ExactPosition)
            .SkipIfTargetDisposedForbidenOrNull(TargetIndex.B)
            .FailAtCondition(() => goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent || storageAgent.Storage.IsEmpty());
        var storeDrop = GeneralActions.Instant("SmartUnload.Store");
        var done = GeneralActions.Instant("SmartUnload.Done");

        decide.OnInit = delegate
        {
            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
            {
                goal.EndGoalWith(GoalCondition.Error);
                return;
            }

            CoordinatedStockpileExecutionStore.ClearActiveDrop(goal, creature);
            if (storageAgent.Storage.IsEmpty())
            {
                JumpToAction(goal, done);
                return;
            }

            JumpToAction(goal, prepareDrop);
        };

        prepareDrop.OnInit = delegate
        {
            if (goal.AgentOwner is not CreatureBase creature ||
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
            {
                goal.EndGoalWith(GoalCondition.Error);
                return;
            }

            if (!UnloadExecutionPlanner.TryPrepareNextDrop(goal, creature, storageAgent.Storage, preferPlannedStorages: false))
            {
                DiagnosticTrace.Info(
                    "unload.exec",
                    $"Failed to prepare unload step for {goal.AgentOwner}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
                    80);
                goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

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
                "unload.exec",
                $"Unload move failed for {goal.AgentOwner}: status={status}, failures={failures}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
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
                goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
            {
                goal.EndGoalWith(GoalCondition.Error);
                return;
            }

            if (!UnloadExecutionPlanner.TryStoreActiveDrop(goal, creature, storageAgent.Storage))
            {
                var failures = CoordinatedStockpileExecutionStore.IncrementDropFailures(goal);
                DiagnosticTrace.Info(
                    "unload.exec",
                    $"Failed to store unload step for {goal.AgentOwner}, failures={failures}, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]",
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
                "unload.exec",
                $"Completing SmartUnloadGoal for {goal.AgentOwner}: carry=0",
                120);
            goal.EndGoalWith(GoalCondition.Succeeded);
        };

        yield return decide;
        yield return prepareDrop;
        yield return goToDrop;
        yield return storeDrop;
        yield return done;
    }

    private static void JumpToAction(Goal goal, GoapAction action)
    {
        JumpToActionMethod.Invoke(goal, new object[] { action });
    }
}
