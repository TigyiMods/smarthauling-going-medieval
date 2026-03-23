using HarmonyLib;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(Goal), "Tick")]
internal static class GoalTickDiscoveryPatch
{
    private static void Prefix(Goal __instance, float deltaTime)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        if (__instance is StockpileHaulingGoal)
        {
            ClusterOwnershipStore.RefreshGoal(__instance);
            DestinationLeaseStore.RefreshGoal(__instance);
            HaulingDecisionTracePatch.RefreshCoordinatedTask(__instance);
        }
    }
}
