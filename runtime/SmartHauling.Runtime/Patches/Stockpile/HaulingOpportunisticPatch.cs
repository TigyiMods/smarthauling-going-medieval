using HarmonyLib;
using NSMedieval.Goap.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(HaulingBaseGoal), "InjectPilesInProximityRange")]
internal static class HaulingOpportunisticPatch
{
    [HarmonyPrefix]
    private static bool Prefix(HaulingBaseGoal __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return true;
        }

        // Stockpile hauling is planned up-front by the hard planner.
        // The vanilla injector rewrites queue A back toward same-type piles.
        return __instance is not StockpileHaulingGoal;
    }
}
