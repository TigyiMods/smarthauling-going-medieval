using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NSEipix;
using NSEipix.Base;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Village;
using NSMedieval.Village.Map.Pathfinding;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(ProductionBaseGoal), "PrepareCollectStep")]
internal static class ProductionCollectSmartPatch
{
    private static readonly AccessTools.FieldRef<ProductionBaseGoal, int> CollectAmountRef =
        AccessTools.FieldRefAccess<ProductionBaseGoal, int>("collectAmount");

    private static readonly AccessTools.FieldRef<ProductionBaseGoal, bool> UseWaterNoPileRef =
        AccessTools.FieldRefAccess<ProductionBaseGoal, bool>("useWaterNoPile");

    private static readonly AccessTools.FieldRef<ProductionBaseGoal, int> WaterAmountRef =
        AccessTools.FieldRefAccess<ProductionBaseGoal, int>("waterAmount");

    private sealed class PendingFilter
    {
        public PendingFilter(ResourceSearchFilter filter, int remaining)
        {
            Filter = filter;
            Remaining = remaining;
        }

        public ResourceSearchFilter Filter { get; }

        public int Remaining { get; set; }
    }

    private static bool Prefix(ProductionBaseGoal __instance, ProductionStepInstance step, ref bool __result)
    {
        if (!RuntimeActivation.IsActive)
        {
            return true;
        }

        MixedCollectPlanStore.Clear(__instance);

        if (step is not ProductionStepCollect stepCollect)
        {
            return true;
        }

        if (!stepCollect.ResourcesAllowedAvailable)
        {
            __result = false;
            return false;
        }

        var missingResources = stepCollect.MissingResources;
        if (missingResources == null || missingResources.Count <= 1)
        {
            return true;
        }

        if (missingResources.Any(stepCollect.HasWaterOnMap))
        {
            return true;
        }

        var storageAgent = __instance.AgentOwner as IStorageAgent;
        var pathfindingAgent = __instance.AgentOwner as IPathfindingAgent;
        var creature = __instance.AgentOwner as CreatureBase;
        var instance = step.OwnerProductionInstance;
        if (storageAgent?.Storage == null || pathfindingAgent == null || creature == null || instance == null)
        {
            return true;
        }

        if (!ProductionTargetOwnershipStore.TryClaim(__instance, creature, instance))
        {
            DiagnosticTrace.Info("prod.collect", $"Skipped for {instance.BlueprintId}: claimed by other worker", 80);
            __result = false;
            return false;
        }

        var pendingFilters = missingResources
            .Where(filter => filter.Count > 0)
            .Select(filter => new PendingFilter(filter, filter.Count))
            .ToList();

        if (pendingFilters.Count > 1)
        {
            DiagnosticTrace.Info("prod.collect", $"Observed for {instance.BlueprintId}: missing={pendingFilters.Count}");
        }

        if (pendingFilters.Count <= 1)
        {
            return true;
        }

        if (!AllFiltersExistOnMap(pendingFilters, stepCollect))
        {
            __result = false;
            return false;
        }

        var queuedTargets = __instance.GetTargetQueue(TargetIndex.A);
        var previouslyQueuedTargets = queuedTargets.ToArray();
        queuedTargets.Clear();

        foreach (var target in previouslyQueuedTargets)
        {
            if (target.ObjectInstance is IReservable reservable)
            {
                MonoSingleton<ReservationManager>.Instance.ReleaseObject(reservable, __instance.AgentOwner);
            }
        }

        var requestedByResourceId = new Dictionary<string, int>();
        var plannedWeight = 0f;
        var plannedAny = storageAgent.Storage.HasOneOrMoreResources();
        List<WorldObject>? foundObjects = null;
        var totalRequested = 0;

        try
        {
            foundObjects = PathfinderUtil.FindNearbyObject(pathfindingAgent, pathfindingAgent.GetGridPosition(), -1f, worldObject =>
            {
                if (AllSatisfied(pendingFilters))
                {
                    return -1;
                }

                if (worldObject.Type != WorldObjectType.ResourcePile || worldObject is not ResourcePileInstance pile)
                {
                    return 0;
                }

                if (!IsValidProductionPile(stepCollect, instance, pile))
                {
                    return 0;
                }

                var storedResource = pile.GetStoredResource();
                if (storedResource == null || storedResource.HasDisposed)
                {
                    return 0;
                }

                var pendingFilter = pendingFilters.FirstOrDefault(filter =>
                    filter.Remaining > 0 && filter.Filter.Check(storedResource, ignoreCount: true));

                if (pendingFilter == null)
                {
                    return 0;
                }

                var carryCapacity = PickupPlanningUtil.GetProjectedCapacity(storageAgent.Storage, storedResource.Blueprint, plannedWeight, plannedAny);
                if (carryCapacity <= 0)
                {
                    return -1;
                }

                var requestedAmount = Mathf.Min(pendingFilter.Remaining, storedResource.Count.Amount, carryCapacity);
                if (requestedAmount <= 0)
                {
                    return 0;
                }

                if (!MonoSingleton<ReservationManager>.Instance.TryReserveObject(pile, __instance.AgentOwner))
                {
                    return 0;
                }

                pendingFilter.Remaining -= requestedAmount;
                plannedWeight += PickupPlanningUtil.GetProjectedWeight(storageAgent.Storage, storedResource.Blueprint, requestedAmount);
                plannedAny = true;
                totalRequested += requestedAmount;

                var resourceId = storedResource.Blueprint.GetID();
                requestedByResourceId[resourceId] = requestedByResourceId.TryGetValue(resourceId, out var current)
                    ? current + requestedAmount
                    : requestedAmount;

                return AllSatisfied(pendingFilters) ? 2 : 1;
            });

            if (totalRequested <= 0 || requestedByResourceId.Count <= 1)
            {
                DiagnosticTrace.Info("prod.collect", $"Fallback for {instance.BlueprintId}: planned={totalRequested}, resources={requestedByResourceId.Count}");
                ReleaseReservations(foundObjects, __instance);
                __result = true;
                return true;
            }

            foreach (var worldObject in foundObjects)
            {
                if (worldObject is ResourcePileInstance pile)
                {
                    queuedTargets.Add(new TargetObject(pile));
                }
            }

            CollectAmountRef(__instance) = totalRequested;
            UseWaterNoPileRef(__instance) = false;
            WaterAmountRef(__instance) = 0;
            MixedCollectPlanStore.Set(__instance, requestedByResourceId);

            SmartHaulingPlugin.Logger.LogInfo(
                $"Mixed collect plan for {instance.BlueprintId}: {string.Join(", ", requestedByResourceId.Select(x => $"{x.Key}={x.Value}"))}");

            __result = true;
            return false;
        }
        finally
        {
            if (foundObjects != null)
            {
                ListPool<WorldObject>.Return(foundObjects);
            }
        }
    }

    private static bool AllFiltersExistOnMap(IEnumerable<PendingFilter> pendingFilters, ProductionStepCollect stepCollect)
    {
        return pendingFilters.All(filter =>
            MonoSingleton<ResourcePileTracker>.Instance.SearchForFilterHits(filter.Filter) || stepCollect.HasWaterOnMap(filter.Filter));
    }

    private static bool AllSatisfied(IEnumerable<PendingFilter> pendingFilters)
    {
        return pendingFilters.All(filter => filter.Remaining <= 0);
    }

    private static bool IsValidProductionPile(ProductionStepCollect stepCollect, ProductionInstance instance, ResourcePileInstance pile)
    {
        if (pile.IsForbidden || pile.PlacedOnAnimalFeeder)
        {
            return false;
        }

        if (pile.InstanceStockpile != null && !pile.InstanceStockpile.CanBeUsedInProduction)
        {
            return false;
        }

        if (pile.InstanceStorage != null && !pile.InstanceStorage.GetOwner.CanBeUsedInProduction)
        {
            return false;
        }

        if (pile is HumanCarcassPileInstance { MarkedForStripping: not false })
        {
            return false;
        }

        var storedResource = pile.GetStoredResource();
        return storedResource != null && instance.ResourceFilter.IsValid(storedResource);
    }

    private static void ReleaseReservations(IEnumerable<WorldObject>? worldObjects, ProductionBaseGoal goal)
    {
        if (worldObjects == null)
        {
            return;
        }

        foreach (var worldObject in worldObjects)
        {
            if (worldObject is IReservable reservable)
            {
                MonoSingleton<ReservationManager>.Instance.ReleaseObject(reservable, goal.AgentOwner);
            }
        }
    }

}
