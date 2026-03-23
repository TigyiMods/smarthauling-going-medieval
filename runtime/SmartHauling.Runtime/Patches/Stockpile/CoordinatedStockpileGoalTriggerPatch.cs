using NSMedieval.Goap;
using NSMedieval.State.WorkerJobs;
using SmartHauling.Runtime.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyLib.HarmonyPatch(typeof(WorkerGoapAgent), nameof(WorkerGoapAgent.Tick))]
internal static class CoordinatedStockpileGoalTriggerPatch
{
    private const int HighestPriorityValue = 1;
    private const string StockpileGoalId = "StockpileHaulingGoal";

    [HarmonyLib.HarmonyPrefix]
    private static void Prefix(WorkerGoapAgent __instance)
    {
        if (!RuntimeActivation.IsActive ||
            __instance == null ||
            __instance.HasDisposed ||
            __instance.GetCurrentGoal() != null ||
            __instance.IsGoalPreparing ||
            __instance.AgentOwner == null)
        {
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

        if (__instance.GetJobPriority(JobType.Hauling) != HighestPriorityValue)
        {
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
