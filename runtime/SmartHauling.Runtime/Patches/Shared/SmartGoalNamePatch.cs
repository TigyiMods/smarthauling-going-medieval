using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using SmartHauling.Runtime.Goals;
using NSMedieval.State;
using NSMedieval.UI.Utils;

namespace SmartHauling.Runtime.Patches;

[HarmonyLib.HarmonyPatch]
internal static class SmartGoalNamePatch
{
    [HarmonyLib.HarmonyPatch(typeof(CreatureBaseUtils), nameof(CreatureBaseUtils.GetLocalizedCurrentActionInfo))]
    [HarmonyLib.HarmonyPostfix]
    private static void GetLocalizedCurrentActionInfoPostfix(CreatureBase creatureBase, ref string __result)
    {
        if (!RuntimeActivation.IsActive ||
            string.IsNullOrWhiteSpace(__result) ||
            !ShouldMarkGoal(creatureBase?.GetGoapAgent()?.GetCurrentGoal()))
        {
            return;
        }

        __result = SmartStatusText.AppendSmartSuffix(__result);
        DiagnosticTrace.Info(
            "goal.name",
            $"Smart goal label remap: localized='{__result}', goal='{creatureBase?.GetGoapAgent()?.GetCurrentGoal()?.GetType().Name}'",
            40);
    }

    private static bool ShouldMarkGoal(Goal? goal)
    {
        if (goal == null)
        {
            return false;
        }

        if (goal is SmartUnloadGoal)
        {
            return true;
        }

        return goal is StockpileHaulingGoal && CoordinatedStockpileTaskStore.TryGet(goal, out _);
    }

}
