using HarmonyLib;
using NSMedieval.Goap;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(WorkerGoapAgent), nameof(WorkerGoapAgent.StartTicker))]
internal static class RuntimeActivationPatch
{
    private static void Postfix(WorkerGoapAgent __instance)
    {
        RuntimeActivation.Activate($"WorkerGoapAgent.StartTicker owner={__instance.AgentOwner}");
    }
}
