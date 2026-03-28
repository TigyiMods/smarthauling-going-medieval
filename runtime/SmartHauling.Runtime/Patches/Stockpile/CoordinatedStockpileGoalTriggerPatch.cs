using NSMedieval.Goap;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyLib.HarmonyPatch(typeof(WorkerGoapAgent), nameof(WorkerGoapAgent.Tick))]
internal static class CoordinatedStockpileGoalTriggerPatch
{
    private const string StockpileGoalId = "StockpileHaulingGoal";

    [HarmonyLib.HarmonyPrefix]
    private static void Prefix(WorkerGoapAgent __instance)
    {
        if (!RuntimeActivation.IsActive ||
            __instance == null ||
            __instance.HasDisposed ||
            __instance.IsGoalPreparing ||
            __instance.AgentOwner == null)
        {
            return;
        }

        var currentGoal = __instance.GetCurrentGoal();
        if (currentGoal != null)
        {
            GoalStallWatchdog.TryAbortStalledGoal(currentGoal);
            return;
        }

        if (__instance.AgentOwner is IStorageAgent { Storage: not null } storageAgent &&
            !storageAgent.Storage.IsEmpty())
        {
            __instance.ForceNextGoal(new SmartUnloadGoal(__instance));
            DiagnosticTrace.Info(
                "unload",
                $"Forced next SmartUnloadGoal for {__instance.AgentOwner}: carry={storageAgent.Storage.GetTotalStoredCount()}",
                80);
            return;
        }

        if (!HaulingGoalPriorityGate.TryAllowForcedHauling(
                __instance.GetJobPriority,
                out var blockingJob,
                out var blockingPriority))
        {
            if (blockingJob.HasValue)
            {
                DiagnosticTrace.Raw(
                    "haul.priority",
                    $"Skipped forced {StockpileGoalId} for {__instance.AgentOwner}: blockingJob={blockingJob.Value}, blockingPriority={blockingPriority:0.##}, haulingPriority={__instance.GetJobPriority(JobType.Hauling):0.##}");
            }

            DiagnosticTrace.Info(
                "haul.trigger",
                $"Skipped smart trigger for {__instance.AgentOwner}: reason=priority-gate, blockingJob={blockingJob?.ToString() ?? "<none>"}, blockingPriority={blockingPriority:0.##}, haulingPriority={__instance.GetJobPriority(JobType.Hauling):0.##}, recent={DescribeRecentGoal(__instance.AgentOwner as CreatureBase)}",
                200);

            return;
        }

        if (!StockpileTaskBoard.HasAssignableTask(__instance))
        {
            DiagnosticTrace.Info(
                "haul.trigger",
                $"Skipped smart trigger for {__instance.AgentOwner}: reason=no-assignable-task, haulingPriority={__instance.GetJobPriority(JobType.Hauling):0.##}, recent={DescribeRecentGoal(__instance.AgentOwner as CreatureBase)}",
                200);
            return;
        }

        // If the board-owned boundary proves too strict, relax the trigger conditions here first.
        // Do not widen downstream StockpileHaulingGoal patches again, or vanilla/player-issued hauling
        // will get hijacked the same way as before.
        var creature = __instance.AgentOwner as CreatureBase;
        if (creature != null)
        {
            CoordinatedStockpileIntentStore.MarkPending(creature);
        }

        __instance.ForceNextGoal(StockpileGoalId);
        DiagnosticTrace.Info(
            "haul.trigger",
            $"Forced next {StockpileGoalId} for {__instance.AgentOwner}: haulingPriority={__instance.GetJobPriority(JobType.Hauling):0.##}, recent={DescribeRecentGoal(creature)}",
            200);
    }

    private static string DescribeRecentGoal(CreatureBase? creature)
    {
        if (creature == null || !RecentGoalOriginStore.TryGetRecent(creature, out var recent))
        {
            return "<none>";
        }

        var age = RuntimeServices.Clock.RealtimeSinceStartup - recent.EndedAt;
        return $"{recent.GoalType}/{recent.Condition} action={recent.ActionId} age={age:0.00}s carry={recent.CarryCount} [{recent.CarrySummary}]";
    }
}

