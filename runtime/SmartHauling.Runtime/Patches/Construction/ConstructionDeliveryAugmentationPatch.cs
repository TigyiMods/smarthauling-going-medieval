using HarmonyLib;
using NSMedieval.BuildingComponents;
using NSMedieval.Goap;
using NSMedieval.State;
using NSMedieval.Utils.Pool.Janitors;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(DeliveryJobManager), nameof(DeliveryJobManager.TryReserveDeliverResourceJobs))]
internal static class ConstructionDeliveryAugmentationPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        DeliveryJobManager __instance,
        HumanoidInstance agent,
        ref PooledList<BaseBuildingInstance> outJobs,
        ref SimpleResourceCount agentResourceOrder,
        ref bool __result)
    {
        if (!RuntimeActivation.IsActive ||
            !__result ||
            agent == null ||
            outJobs.Count == 0 ||
            agentResourceOrder.Equals(default(SimpleResourceCount)))
        {
            return;
        }

        var addedCount = ConstructionDeliveryJobAugmentor.Augment(
            __instance,
            agent,
            outJobs,
            ref agentResourceOrder);
        if (addedCount <= 0)
        {
            return;
        }

        DiagnosticTrace.Info(
            "construct.plan",
            $"Augmented construction delivery for {agent}: added={addedCount}, targets={outJobs.Count}, resource={agentResourceOrder.BlueprintId}, amount={agentResourceOrder.Amount}",
            80);
    }
}
