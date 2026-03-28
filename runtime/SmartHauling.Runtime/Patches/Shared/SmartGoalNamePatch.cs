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
        var goal = creatureBase?.GetGoapAgent()?.GetCurrentGoal();
        if (!RuntimeActivation.IsActive ||
            string.IsNullOrWhiteSpace(__result) ||
            !ShouldMarkGoal(goal))
        {
            return;
        }

        __result = SmartStatusText.AppendSmartSuffix(RemapGoalLabel(goal!, __result));
        DiagnosticTrace.Info(
            "goal.name",
            $"Smart goal label remap: localized='{__result}', goal='{goal?.GetType().Name}'",
            40);
    }

    private static string RemapGoalLabel(Goal goal, string localizedText)
    {
        if (goal is SmartUnloadGoal)
        {
            return SmartStatusText.NormalizeGoalDisplayText(
                localizedText,
                SmartHaulingLocalization.SmartUnloadGoalNameTerm,
                SmartHaulingLocalization.DefaultSmartUnloadGoalName);
        }

        if (goal is StockpileHaulingGoal)
        {
            return SmartStatusText.NormalizeGoalDisplayText(
                localizedText,
                SmartHaulingLocalization.StockpileHaulingGoalNameTerm,
                SmartHaulingLocalization.DefaultStockpileHaulingGoalName);
        }

        return SmartStatusText.ResolveDisplayText(localizedText);
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
