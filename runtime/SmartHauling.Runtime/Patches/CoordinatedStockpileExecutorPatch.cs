using System.Collections.Generic;
using HarmonyLib;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(HaulingBaseGoal), "GetNextAction")]
internal static class CoordinatedStockpileExecutorPatch
{
    [HarmonyPostfix]
    private static void Postfix(HaulingBaseGoal __instance, ref IEnumerable<GoapAction> __result)
    {
        if (!RuntimeActivation.IsActive || __instance is not StockpileHaulingGoal stockpileGoal)
        {
            return;
        }

        __result = CoordinatedStockpileExecutor.Build(stockpileGoal);
    }
}
