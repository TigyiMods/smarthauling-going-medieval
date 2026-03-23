using HarmonyLib;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
namespace SmartHauling.Runtime.Patches;

[HarmonyPatch]
internal static class GoalLifecycleTracePatch
{
    [HarmonyPatch(typeof(Goal), nameof(Goal.Start))]
    [HarmonyPostfix]
    private static void StartPostfix(Goal __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        GoalStallWatchdog.Clear(__instance);

        if (!IsRelevant(__instance))
        {
            return;
        }

        DiagnosticTrace.Info("goal.start", $"{__instance.GetType().Name} started for {__instance.AgentOwner}", 40);
    }

    [HarmonyPatch(typeof(Goal), nameof(Goal.EndGoalWith))]
    [HarmonyPrefix]
    private static void EndGoalWithPrefix(Goal __instance, GoalCondition condition)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        MarkFailedStockpileSources(__instance, condition);
        PreserveCarryPriority(__instance, condition);
        GoalSourcePriorityStore.Clear(__instance);
        GoalStallWatchdog.Clear(__instance);
        StockpileDestinationPlanStore.Clear(__instance);
        CoordinatedStockpileTaskStore.Clear(__instance);
        DestinationLeaseStore.ReleaseGoal(__instance);
        StorageEmptyRecoveryStore.Clear(__instance);
        CoordinatedStockpileExecutionStore.Clear(__instance, __instance.AgentOwner as CreatureBase);
        HaulingDecisionTracePatch.ReleaseCoordinatedTask(__instance);

        if (__instance is StockpileHaulingGoal)
        {
            ClusterOwnershipStore.ReleaseGoal(__instance);
        }

        if (!IsRelevant(__instance))
        {
            return;
        }

        DiagnosticTrace.Info(
            "goal.end",
            $"{__instance.GetType().Name} ended with {condition}, action={__instance.CurrentAction?.Id ?? "<none>"}",
            40);
    }

    private static bool IsRelevant(Goal goal)
    {
        return goal is ProductionBaseGoal || goal is StockpileHaulingGoal;
    }

    private static void PreserveCarryPriority(Goal goal, GoalCondition condition)
    {
        if (goal.AgentOwner is not CreatureBase creature ||
            goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return;
        }

        if (storageAgent.Storage.IsEmpty() || condition == GoalCondition.Succeeded)
        {
            UnloadCarryContextStore.Clear(creature);
            return;
        }

        if (GoalSourcePriorityStore.TryGet(goal, out var sourcePriority))
        {
            UnloadCarryContextStore.SetSourcePriority(creature, sourcePriority);
        }
    }

    private static void MarkFailedStockpileSources(Goal goal, GoalCondition condition)
    {
        if (goal is not StockpileHaulingGoal ||
            condition == GoalCondition.Succeeded ||
            goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent ||
            !storageAgent.Storage.IsEmpty())
        {
            return;
        }

        var actionId = goal.CurrentAction?.Id;
        if (actionId != "GoToTarget" &&
            actionId != "PickupResourceFromPile" &&
            actionId != "CompleteIfOwnerStorageIsEmpty")
        {
            return;
        }

        var currentPile = goal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>();
        if (currentPile == null)
        {
            return;
        }

        if (HaulFailureBackoffStore.IsCoolingDown(currentPile))
        {
            return;
        }

        var marked = actionId == "CompleteIfOwnerStorageIsEmpty"
            ? HaulFailureBackoffStore.MarkEmptyPile(new[] { currentPile })
            : HaulFailureBackoffStore.MarkFailed(new[] { currentPile });
        HaulingDecisionTracePatch.MarkTaskFailed(currentPile);
        if (marked > 0)
        {
            DiagnosticTrace.Info(
                "haul.backoff",
                $"Cooling down failed source {currentPile.BlueprintId} after {actionId} for {goal.AgentOwner}",
                80);
        }
    }
}
