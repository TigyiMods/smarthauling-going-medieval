using HarmonyLib;
using NSEipix;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.Sound;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch]
internal static class ResourceActionPatch
{
    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> PickedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("PickedCount");

    private static readonly System.Reflection.MethodInfo ClearTargetsQueueMethod =
        AccessTools.Method(typeof(Goal), "ClearTargetsQueue", new[] { typeof(TargetIndex) })!;

    [HarmonyPatch(
        typeof(ResourceActions),
        nameof(ResourceActions.PickupResourceFromPile),
        new[] { typeof(TargetIndex), typeof(System.Func<Resource, int>), typeof(System.Action<Resource, int>), typeof(bool), typeof(Storage) })]
    [HarmonyPostfix]
    private static void PickupResourceFromPilePostfix(
        ref GoapAction __result,
        TargetIndex index,
        System.Func<Resource, int> requestedAmount,
        System.Action<Resource, int>? successCallback,
        bool onlySameResourceType,
        Storage? destinationStorage)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("PickupResourceFromPile");
        action.OnInit = delegate
        {
            var storageAgent = (IStorageAgent)action.AgentOwner;
            var storage = destinationStorage ?? storageAgent.Storage;
            var pile = action.Goal.GetTarget(index).GetObjectAs<ResourcePileInstance>();
            var carryBeforeEarly = storage?.GetTotalStoredCount() ?? 0;
            if (storage == null || pile == null || pile.HasDisposed)
            {
                if (TryFinishHarvestAndProceedToStorage(action, storage, pile, "missing-or-disposed-target"))
                {
                    return;
                }

                DiagnosticTrace.Info(
                    "pickup",
                    $"Pickup failed for {action.AgentOwner}: reason=missing-or-disposed-target, carryBefore={carryBeforeEarly}, target={pile?.BlueprintId ?? "<none>"}");
                action.Goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            var singleResource = storage.GetSingleResource();
            var carriedResourcesBefore = CarrySummaryUtil.Snapshot(storage);
            var carryBefore = storage.GetTotalStoredCount();
            var carryBeforeSummary = CarrySummaryUtil.Summarize(carriedResourcesBefore);
            var hasMixedPlan = MixedCollectPlanStore.HasMixedPlan(action.Goal);
            var requested = MixedCollectPlanStore.GetRequestedAmount(action.Goal, pile.Blueprint, requestedAmount(pile.Blueprint), out var usedPlan);
            var enforceSameType = (onlySameResourceType || carryBefore > 0) && !hasMixedPlan;

            if (!hasMixedPlan && carriedResourcesBefore.Any(resource => resource.Blueprint != pile.Blueprint))
            {
                DiagnosticTrace.Info(
                    "pickup",
                    $"Blocked cross-type pickup for {action.AgentOwner}: incoming={pile.BlueprintId}, carryBefore={carryBefore}, carried=[{carryBeforeSummary}]");
                if (TryFinishHarvestAndProceedToStorage(action, storage, pile, "blocked-cross-type"))
                {
                    return;
                }

                action.Goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            if (singleResource != null && enforceSameType && singleResource.Blueprint != pile.Blueprint)
            {
                DiagnosticTrace.Info(
                    "pickup",
                    $"Rejected pickup for {action.AgentOwner}: incoming={pile.BlueprintId}, firstCarried={singleResource.BlueprintId}, carryBefore={carryBefore}, mixedPlan={hasMixedPlan}");
                if (TryFinishHarvestAndProceedToStorage(action, storage, pile, "same-type-mismatch"))
                {
                    return;
                }

                action.Goal.EndGoalWith(GoalCondition.Error);
                return;
            }

            var sourceStorage = pile.GetStorage();
            if (requested <= 0)
            {
                if (usedPlan)
                {
                    DiagnosticTrace.Info(
                        "pickup",
                        $"Skipped planned pickup for {action.AgentOwner}: resource={pile.BlueprintId}, requested=0, queueRemaining={action.Goal.GetTargetQueue(index).Count}, carry={storage.GetTotalStoredCount()}, carried=[{CarrySummaryUtil.Summarize(storage)}]",
                        120);
                    action.Complete(ActionCompletionStatus.Success);
                    return;
                }

                requested = sourceStorage.GetSingleResource().Amount;
            }

            var transferred = sourceStorage.TransferTo(storage, pile.Blueprint, requested);
            if (transferred <= 0)
            {
                DiagnosticTrace.Info(
                    "pickup",
                    $"Pickup transferred nothing for {action.AgentOwner}: resource={pile.BlueprintId}, requested={requested}, carryBefore={carryBefore}, carriedBefore=[{carryBeforeSummary}], sourceAmount={sourceStorage.GetSingleResource()?.Amount ?? 0}");
                if (TryFinishHarvestAndProceedToStorage(action, storage, pile, "transfer-zero"))
                {
                    return;
                }

                action.Goal.EndGoalWith(GoalCondition.Incompletable);
                return;
            }

            if (usedPlan)
            {
                MixedCollectPlanStore.Consume(action.Goal, pile.Blueprint, transferred);
            }

            DiagnosticTrace.Info(
                "pickup",
                $"Pickup from {pile.BlueprintId} by {action.AgentOwner}: requested={requested}, transferred={transferred}, mixedPlan={usedPlan}, carryBefore={carryBefore}, carryAfter={storage.GetTotalStoredCount()}, carriedBefore=[{carryBeforeSummary}], carriedAfter=[{CarrySummaryUtil.Summarize(storage)}]");

            action.Complete(ActionCompletionStatus.Success);
            UpdateHaulingPickedCount(action.Goal, storage);
            successCallback?.Invoke(pile.Blueprint, transferred);
            MonoSingleton<AudioManager>.Instance.PlaySoundAtPosition("ObjectPickup", pile.WorldPosition);
        };
        action.FailIfTargetIsNotType<ResourcePileInstance>(index);
        action.FailIfResourcePileHasNoResources(index);
        __result = action;
    }

    private static bool TryFinishHarvestAndProceedToStorage(
        GoapAction action,
        Storage? storage,
        ResourcePileInstance? currentPile,
        string reason)
    {
        if (storage == null || storage.GetTotalStoredCount() <= 0 || action.Goal is not HaulingBaseGoal haulingGoal)
        {
            return false;
        }

        ReleasePendingPickupTargets(action.Goal, action.AgentOwner, currentPile);
        ClearTargetsQueue(action.Goal, TargetIndex.A);
        PickedCountRef(haulingGoal) = storage.GetTotalStoredCount();
        DiagnosticTrace.Info(
            "pickup",
            $"Soft-finished harvest for {action.AgentOwner}: reason={reason}, carry={storage.GetTotalStoredCount()}, carried=[{CarrySummaryUtil.Summarize(storage)}]");
        action.Complete(ActionCompletionStatus.Success);
        return true;
    }

    private static void ReleasePendingPickupTargets(Goal goal, IGoapAgentOwner owner, ResourcePileInstance? currentPile)
    {
        if (currentPile != null)
        {
            RuntimeServices.Reservations.ReleaseObject(currentPile, owner);
            RuntimeServices.Reservations.ReleaseAll(currentPile);
        }

        foreach (var target in goal.GetTargetQueue(TargetIndex.A))
        {
            if (target.ObjectInstance is ResourcePileInstance queuedPile)
            {
                RuntimeServices.Reservations.ReleaseAll(queuedPile);
            }
        }
    }

    private static void ClearTargetsQueue(Goal goal, TargetIndex index)
    {
        ClearTargetsQueueMethod.Invoke(goal, new object[] { index });
    }

    [HarmonyPatch(typeof(ResourceActions), nameof(ResourceActions.DeliverProductionResource))]
    [HarmonyPostfix]
    private static void DeliverProductionResourcePostfix(ref GoapAction __result, TargetIndex index)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("DeliverProductionResource");
        action.OnInit = delegate
        {
            var target = action.Goal.GetTarget(index);
            var storageAgent = (IStorageAgent)action.AgentOwner;
            var productionComponent = target.GetObjectAs<ProductionComponentInstance>();
            var storage = storageAgent.Storage;

            if (storage == null || storage.GetSingleResource() == null)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            if (productionComponent == null || productionComponent.HasDisposed || productionComponent.ProductionSystemInstance?.CurrentProduction == null)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            var currentProduction = productionComponent.ProductionSystemInstance.CurrentProduction;
            var carriedResources = CarrySummaryUtil.Snapshot(storage);
            var deliveredAny = false;

            foreach (var carriedResource in carriedResources)
            {
                if (carriedResource == null || carriedResource.HasDisposed)
                {
                    continue;
                }

                if (!CanDeliverToProduction(currentProduction, carriedResource))
                {
                    continue;
                }

                var carryBefore = storage.GetTotalStoredCount();
                var carriedBeforeSummary = CarrySummaryUtil.Summarize(storage);
                var taken = storage.Take(carriedResource.Blueprint, carriedResource.Amount);
                if (taken == null)
                {
                    continue;
                }

                var deliveredAmount = taken.Amount;
                if (taken is CarcassResourceInstance carcassResource)
                {
                    carcassResource.DropInventory(storageAgent.GetPosition().ToGridVec3Int());
                }

                currentProduction.DeliverResource(taken);
                deliveredAny = true;
                if (action.AgentOwner is CreatureBase creature && storage.GetTotalStoredCount() == 0)
                {
                    UnloadCarryContextStore.Clear(creature);
                }

                DiagnosticTrace.Info(
                    "prod.deliver",
                    $"Delivered {taken.BlueprintId}:{deliveredAmount} to {currentProduction.BlueprintId} by {action.AgentOwner}, carryBefore={carryBefore}, carryAfter={storage.GetTotalStoredCount()}, carriedBefore=[{carriedBeforeSummary}], carriedAfter=[{CarrySummaryUtil.Summarize(storage)}]");
            }

            if (deliveredAny)
            {
                MixedCollectPlanStore.Clear(action.Goal);
                action.Complete(ActionCompletionStatus.Success);
                return;
            }

            action.Complete(ActionCompletionStatus.Error);
        };
        __result = action;
    }

    [HarmonyPatch(typeof(ProductionBaseGoal), nameof(ProductionBaseGoal.EndGoalWith))]
    [HarmonyPrefix]
    private static void EndGoalWithPrefix(ProductionBaseGoal __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        MixedCollectPlanStore.Clear(__instance);
        ProductionTargetOwnershipStore.ReleaseGoal(__instance);
    }

    [HarmonyPatch(typeof(HaulingBaseGoal), nameof(HaulingBaseGoal.EndGoalWith))]
    [HarmonyPrefix]
    private static void HaulingEndGoalWithPrefix(HaulingBaseGoal __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        MixedCollectPlanStore.Clear(__instance);
    }

    [HarmonyPatch(typeof(GoapAction), nameof(GoapAction.Complete))]
    [HarmonyPostfix]
    private static void GoapActionCompletePostfix(GoapAction __instance, ActionCompletionStatus status)
    {
        if (!RuntimeActivation.IsActive ||
            status != ActionCompletionStatus.Success ||
            !string.Equals(__instance.Id, "PickupResourceFromPile", System.StringComparison.Ordinal) ||
            __instance.Goal is not HaulingBaseGoal haulingGoal ||
            !MixedCollectPlanStore.HasMixedPlan(haulingGoal) ||
            __instance.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return;
        }

        PickedCountRef(haulingGoal) = storageAgent.Storage.GetTotalStoredCount();
        DiagnosticTrace.Info(
            "pickup",
            $"Reconciled hauling picked count to {PickedCountRef(haulingGoal)} after completion, carried=[{CarrySummaryUtil.Summarize(storageAgent.Storage)}]");
    }

    private static bool CanDeliverToProduction(ProductionInstance production, ResourceInstance resource)
    {
        return MatchesAny(production.Blueprint.Recipe, resource) || MatchesAny(production.Blueprint.SecondaryRecipe, resource);
    }

    private static bool MatchesAny(IEnumerable<NSEipix.Model.KeyIntPair> recipe, ResourceInstance resource)
    {
        foreach (var ingredient in recipe)
        {
            var ingredientId = ingredient.GetID();
            if (int.TryParse(ingredientId, out var categoryId))
            {
                if ((resource.Blueprint.Category & (NSMedieval.Types.ResourceCategory)categoryId) != 0)
                {
                    return true;
                }

                continue;
            }

            if (resource.BlueprintId == ingredientId)
            {
                return true;
            }
        }

        return false;
    }

    private static void UpdateHaulingPickedCount(Goal goal, Storage storage)
    {
        if (goal is not HaulingBaseGoal haulingGoal || !MixedCollectPlanStore.HasMixedPlan(goal))
        {
            return;
        }

        PickedCountRef(haulingGoal) = storage.GetTotalStoredCount();
        DiagnosticTrace.Info("pickup", $"Updated hauling picked count to {PickedCountRef(haulingGoal)}");
    }
}
