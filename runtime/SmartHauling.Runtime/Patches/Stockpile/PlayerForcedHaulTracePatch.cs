using HarmonyLib;
using NSMedieval.AdditionalMenuItems;
using NSMedieval.State;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(AdditionalMenuPrioritiseItem), "ForceGoal", new[] { typeof(string), typeof(IReservable), typeof(HumanoidInstance) })]
internal static class PlayerForcedHaulTracePatch
{
    private static readonly System.Reflection.MethodInfo GetSelectedWorkerMethod =
        AccessTools.Method(typeof(NSMedieval.AdditionalMenuItemBase), "GetSelectedWorker")!;

    [HarmonyPrefix]
    private static void Prefix(
        AdditionalMenuPrioritiseItem __instance,
        string goalId,
        IReservable setPreferredReservable,
        HumanoidInstance goalExecutor)
    {
        if (!RuntimeActivation.IsActive ||
            !string.Equals(goalId, "StockpileHaulingGoal", StringComparison.Ordinal) ||
            setPreferredReservable is not ResourcePileInstance pile)
        {
            return;
        }

        var worker = goalExecutor ?? GetSelectedWorker(__instance);
        if (worker is not CreatureBase creature)
        {
            return;
        }

        PlayerForcedHaulIntentStore.MarkPending(creature, pile);
        DiagnosticTrace.Info(
            "haul.force",
            $"Marked player-forced stockpile haul for {creature}: anchor={pile.BlueprintId}, pos={FormatPosition(pile.GetPosition())}",
            120);
    }

    private static HumanoidInstance GetSelectedWorker(AdditionalMenuPrioritiseItem item)
    {
        return (HumanoidInstance)GetSelectedWorkerMethod.Invoke(item, Array.Empty<object>());
    }

    private static string FormatPosition(UnityEngine.Vector3 position)
    {
        return $"({position.x:0.0},{position.y:0.0},{position.z:0.0})";
    }
}

