using NSMedieval.Goap;
using NSMedieval.State.WorkerJobs;
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

            return;
        }

        if (!StockpileTaskBoard.HasAssignableTask(__instance))
        {
            return;
        }

        __instance.ForceNextGoal(StockpileGoalId);
        DiagnosticTrace.Info(
            "haul.plan",
            $"Forced next {StockpileGoalId} for {__instance.AgentOwner}: haulingPriority=Highest",
            80);
    }
}
