using HarmonyLib;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using NSMedieval.Village.Map.Pathfinding;

namespace SmartHauling.Runtime;

internal static class HaulSourcePolicy
{
    public static bool CanReachPile(StockpileHaulingGoal goal, ResourcePileInstance pile)
    {
        if (pile == null || pile.HasDisposed)
        {
            return false;
        }

        if (goal?.AgentOwner is not IPathfindingAgent pathfindingAgent)
        {
            return true;
        }

        return PathfinderUtil.GetClosestReachable(
            pathfindingAgent,
            new[] { pile },
            target => ReferenceEquals(target, pile),
            _ => 0f) is ResourcePileInstance;
    }

    public static bool ValidatePile(HaulingBaseGoal goal, ResourcePileInstance pile)
    {
        if (goal == null || pile == null || pile.HasDisposed)
        {
            return false;
        }

        var method = AccessTools.Method(goal.GetType(), "ValidatePile");
        return method != null && method.Invoke(goal, new object[] { pile }) is bool result && result;
    }

    public static string DescribeInvalidPickupReason(StockpileHaulingGoal goal, ResourcePileInstance? pile)
    {
        if (pile == null)
        {
            return "missing-target";
        }

        if (pile.HasDisposed)
        {
            return "disposed";
        }

        var resource = pile.GetStoredResource();
        if (resource == null || resource.HasDisposed || resource.Amount <= 0)
        {
            return "empty";
        }

        if (HaulFailureBackoffStore.IsCoolingDown(pile))
        {
            return "cooldown";
        }

        if (goal.AgentOwner is CreatureBase creature)
        {
            if (!ClusterOwnershipStore.CanUsePile(creature, pile))
            {
                return "claimed";
            }

            if (!CanReachPile(goal, pile))
            {
                return "unreachable";
            }
        }

        return ValidatePile(goal, pile) ? "unknown" : "validate";
    }

    public static bool CanUseAsCentralHaulSource(ResourcePileInstance? pile, IEnumerable<IStorage> storageCandidates)
    {
        if (pile == null || pile.HasDisposed)
        {
            return false;
        }

        if (pile.PlacedOnStorage == null)
        {
            return true;
        }

        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
        if (sourcePriority == ZonePriority.None)
        {
            return false;
        }

        var storedResource = pile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return false;
        }

        return storageCandidates.Any(storage =>
            storage != null &&
            !ReferenceEquals(storage, pile.PlacedOnStorage) &&
            !storage.HasDisposed &&
            !storage.Underwater &&
            !storage.IsOnFire &&
            storage.Priority > sourcePriority &&
            storage.ResourcesFilter.IsValid(storedResource));
    }
}
