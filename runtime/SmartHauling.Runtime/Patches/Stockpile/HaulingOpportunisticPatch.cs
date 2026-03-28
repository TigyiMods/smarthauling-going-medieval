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

        // Only board-owned stockpile goals should bypass the vanilla opportunistic injector.
        // Any other stockpile haul remains vanilla, including player-prioritized or cleanup hauls.
        return __instance is not StockpileHaulingGoal stockpileGoal ||
               !CoordinatedStockpileTaskStore.TryGet(stockpileGoal, out _);
    }
}
