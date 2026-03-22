using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.State.WorkerJobs;

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
