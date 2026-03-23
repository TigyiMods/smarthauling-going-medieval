using HarmonyLib;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Construction;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class ConstructionDeliveryJobAugmentor
{
    private const int MaxAugmentedBuildingsCount = 10;
    private const float MaxAugmentedBuildingDistance = 10f;

    private static readonly AccessTools.FieldRef<DeliveryJobManager, object> JobsLockRef =
        AccessTools.FieldRefAccess<DeliveryJobManager, object>("jobsLock");

    private static readonly AccessTools.FieldRef<DeliveryJobManager, List<CreateVoxelJob>> JobsRef =
        AccessTools.FieldRefAccess<DeliveryJobManager, List<CreateVoxelJob>>("jobs");

    public static int Augment(
        DeliveryJobManager deliveryJobManager,
        HumanoidInstance agent,
        PooledList<BaseBuildingInstance> reservedJobs,
        ref SimpleResourceCount agentResourceOrder)
    {
        if (deliveryJobManager == null ||
            agent == null ||
            reservedJobs.Count == 0 ||
            agentResourceOrder.Equals(default(SimpleResourceCount)) ||
            agentResourceOrder.Blueprint == null ||
            agent.Storage == null)
        {
            return 0;
        }

        var maxCarryAmount = agent.Storage.GetMaximumStorableCount(agentResourceOrder.Blueprint);
        if (maxCarryAmount <= 0 ||
            agentResourceOrder.Amount >= maxCarryAmount ||
            reservedJobs.Count >= MaxAugmentedBuildingsCount)
        {
            return 0;
        }

        var anchorBuilding = reservedJobs[0];
        if (anchorBuilding == null || anchorBuilding.HasDisposed)
        {
            return 0;
        }

        var reservedSet = new HashSet<BaseBuildingInstance>(reservedJobs, ReferenceEqualityComparer<BaseBuildingInstance>.Instance);
        var addedCount = 0;
        var agentSkillLevel = agent.Skills.GetSkill(SkillType.Construction)?.Level ?? 0;
        var jobsLock = JobsLockRef(deliveryJobManager);
        if (jobsLock == null)
        {
            return 0;
        }

        lock (jobsLock)
        {
            var jobs = JobsRef(deliveryJobManager);
            if (jobs == null || jobs.Count == 0)
            {
                return 0;
            }

            foreach (var job in jobs)
            {
                var building = job.Building;
                if (building == null ||
                    building.HasDisposed ||
                    reservedSet.Contains(building) ||
                    !CanAugmentBuilding(agent, building, agentSkillLevel, anchorBuilding, agentResourceOrder.Blueprint))
                {
                    continue;
                }

                var matchingOrder = GetMatchingResourceOrder(agent, building, agentResourceOrder.Blueprint);
                if (matchingOrder == null ||
                    matchingOrder.Value.Equals(default(SimpleResourceCount)) ||
                    !MonoSingleton<ReservationManager>.Instance.TryReserveObject(building, agent))
                {
                    continue;
                }

                var matchingOrderValue = matchingOrder.Value;

                reservedJobs.Add(building);
                reservedSet.Add(building);
                addedCount++;

                agentResourceOrder = new SimpleResourceCount(
                    agentResourceOrder.Blueprint,
                    Mathf.Min(maxCarryAmount, agentResourceOrder.Amount + matchingOrderValue.Amount));

                if (agentResourceOrder.Amount >= maxCarryAmount ||
                    reservedJobs.Count >= MaxAugmentedBuildingsCount)
                {
                    break;
                }
            }
        }

        return addedCount;
    }

    private static bool CanAugmentBuilding(
        HumanoidInstance agent,
        BaseBuildingInstance building,
        int agentSkillLevel,
        BaseBuildingInstance anchorBuilding,
        Resource blueprint)
    {
        if (building.IsForbidden ||
            building.IsOnFire ||
            building.FactionOwnership != FactionOwnership.Player ||
            building.ConstructionPhase == ConstructionPhase.Foundation ||
            building.IsMoveBlueprint ||
            !building.HasStabilityToBuild ||
            !building.IsBlueprintOnClearNode())
        {
            return false;
        }

        if (Vector3.Distance(anchorBuilding.GetPosition(), building.GetPosition()) > MaxAugmentedBuildingDistance)
        {
            return false;
        }

        var minBuildSkillRequired = building.Blueprint.MinBuildSkillRequired;
        if (minBuildSkillRequired > 0 && agentSkillLevel < minBuildSkillRequired)
        {
            return false;
        }

        if (MonoSingleton<ReservationManager>.Instance.IsReserved(building) ||
            !MonoSingleton<ReservationManager>.Instance.CanReserve(building, agent))
        {
            return false;
        }

        if (!PathfinderUtil.IsPathPossible(agent, building, preferEmptyNodes: true, WorldDirection.None, out var reachedPosition))
        {
            return false;
        }

        var node = VillageManager.ActiveVillage.Map.GetNode(reachedPosition);
        var nodeAbove = node.GetNodeAbove();
        if (node.WaterLevel == WaterDepthLevel.High &&
            nodeAbove != null &&
            nodeAbove.IsWater &&
            nodeAbove.WaterLevel != WaterDepthLevel.Low)
        {
            return false;
        }

        return GetMatchingResourceOrder(agent, building, blueprint) != null;
    }

    private static SimpleResourceCount? GetMatchingResourceOrder(
        HumanoidInstance agent,
        BaseBuildingInstance building,
        Resource blueprint)
    {
        var resourceOrder = building.GetResourceOrder(agent);
        if (resourceOrder == null)
        {
            return null;
        }

        foreach (var item in resourceOrder)
        {
            if (item.Blueprint == blueprint)
            {
                return item;
            }
        }

        return null;
    }
}
