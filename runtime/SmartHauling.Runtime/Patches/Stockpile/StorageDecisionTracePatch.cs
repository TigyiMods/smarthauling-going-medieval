using System.Collections;
using HarmonyLib;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.State;
using NSMedieval.Stockpiles;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch]
internal static class StorageDecisionTracePatch
{
    private static readonly System.Reflection.FieldInfo ActionsField =
        AccessTools.Field(typeof(Goal), "actions")!;

    private static readonly System.Reflection.MethodInfo JumpToActionMethod =
        AccessTools.Method(typeof(Goal), "JumpToAction", new[] { typeof(GoapAction) })!;

    [HarmonyPatch(typeof(StorageActions), nameof(StorageActions.FindBestStorage))]
    [HarmonyPostfix]
    private static void FindBestStoragePostfix(ref GoapAction __result, TargetIndex outputQueue, ZonePriority minimumPriority, bool enablePriorityFallback)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("FindBestStorage");
        var usedAnchorStorage = false;
        var usedDestinationPlan = false;
        var effectiveMinimumPriority = minimumPriority;
        var sourcePriority = ZonePriority.None;
        var chosenStoragePriority = ZonePriority.None;
        var chosenStorageDescription = "<none>";
        var candidateSummary = "<none>";
        action.OnInit = delegate
        {
            var storageAgent = action.AgentOwner as IStorageAgent;
            var creature = action.AgentOwner as CreatureBase;
            var singleResource = storageAgent?.Storage?.GetSingleResource();
            if (creature == null || singleResource == null)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            if (creature != null && HaulingPriorityRules.TryGetGoalSourcePriority(action.Goal, creature, out sourcePriority))
            {
                effectiveMinimumPriority = HaulingPriorityRules.GetRequiredMinimumPriority(sourcePriority, minimumPriority);
            }

            IStorage? storage = null;
            IStorage? preferredStorage = null;
            if (action.Goal is HaulingBaseGoal haulingGoal &&
                MixedCollectPlanStore.TryGetAnchorStorage(haulingGoal, out var anchorStorage) &&
                !anchorStorage.Underwater &&
                !anchorStorage.IsOnFire &&
                HaulingPriorityRules.CanMoveToPriority(sourcePriority, anchorStorage.Priority) &&
                anchorStorage.CanStore(singleResource, creature))
            {
                preferredStorage = anchorStorage;
            }

            var preferredOrder = StockpileDestinationPlanStore.TryGetActiveStorages(
                action.Goal,
                singleResource.BlueprintId,
                out var plannedStorages)
                ? plannedStorages
                : null;
            usedDestinationPlan = preferredOrder != null && preferredOrder.Count > 0;

            var candidatePlan = StorageCandidatePlanner.BuildPlan(
                action.Goal,
                creature!,
                singleResource,
                effectiveMinimumPriority,
                sourcePriority,
                enablePriorityFallback,
                Math.Max(1, singleResource.Amount),
                preferredStorage,
                preferredOrder);
            storage = candidatePlan.Primary?.Storage;
            usedAnchorStorage = preferredStorage != null && storage != null && ReferenceEquals(storage, preferredStorage);
            candidateSummary = candidatePlan.Summarize();
            if (storage == null)
            {
                action.Complete(ActionCompletionStatus.Fail);
                return;
            }

            chosenStoragePriority = storage.Priority;
            chosenStorageDescription = DescribeStorage(storage);
            ClearTargetsQueue(action.Goal, outputQueue);
            QueueTarget(action.Goal, outputQueue, new TargetObject(storage));
            action.Complete(ActionCompletionStatus.Success);
        };
        action.OnComplete = status =>
        {
            var storageAgent = action.AgentOwner as IStorageAgent;
            var singleResource = storageAgent?.Storage?.GetSingleResource();
            var queueCount = action.Goal.GetTargetQueue(outputQueue).Count;
            DiagnosticTrace.Info(
                "haul.storage",
                () => $"FindBestStorage status={status}, goal={action.Goal.GetType().Name}, owner={action.AgentOwner}, resource={singleResource?.BlueprintId ?? "<none>"}, amount={singleResource?.Amount ?? 0}, carry={storageAgent?.Storage?.GetTotalStoredCount() ?? 0}, carried=[{CarrySummaryUtil.Summarize(storageAgent?.Storage)}], queue={queueCount}, minPriority={minimumPriority}, effectiveMinPriority={effectiveMinimumPriority}, sourcePriority={sourcePriority}, targetPriority={chosenStoragePriority}, fallback={enablePriorityFallback}, source={(usedAnchorStorage ? "anchor" : usedDestinationPlan ? "plan" : "planner")}, storage={chosenStorageDescription}, candidates={candidateSummary}");
        };
        __result = action;
    }

    [HarmonyPatch(typeof(StorageActions), nameof(StorageActions.CompleteIfOwnerStorageIsEmpty))]
    [HarmonyPostfix]
    private static void CompleteIfOwnerStorageIsEmptyPostfix(ref GoapAction __result, GoalCondition status)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("CompleteIfOwnerStorageIsEmpty");
        action.OnInit = delegate
        {
            var storageAgent = action.AgentOwner as IStorageAgent;
            var storage = storageAgent?.Storage;
            var singleResource = storage?.GetSingleResource();
            if (singleResource != null && singleResource.Count.Amount > 0)
            {
                action.Complete(ActionCompletionStatus.Success);
                return;
            }

            if (action.Goal is StockpileHaulingGoal haulingGoal &&
                TryRecoverEmptyStorageHaul(haulingGoal))
            {
                action.Complete(ActionCompletionStatus.Success);
                return;
            }

            action.Goal.EndGoalWith(status);
        };
        __result = action;
    }

    [HarmonyPatch(typeof(StorageActions), nameof(StorageActions.ReserveAndQueueStoragePlaces))]
    [HarmonyPostfix]
    private static void ReserveAndQueueStoragePlacesPostfix(
        ref GoapAction __result,
        TargetIndex storageIndex,
        TargetIndex outputQueueIndex,
        Action<IStorage, Vec3Int>? onStorageReserved,
        GoapAction? jumpOnFail)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("ReserveAndQueueStoragePlaces");
        var requestedTotal = 0;
        var reservedTotal = 0;
        var requestedSummary = "<none>";
        var storageDescription = "<none>";
        var usedStorageCount = 0;
        action.OnInit = delegate
        {
            var creature = action.AgentOwner as CreatureBase;
            var storageAgent = action.AgentOwner as IStorageAgent;
            var storage = action.Goal.GetTarget(storageIndex).ObjectInstance as IStorage;
            if (creature == null || storageAgent?.Storage == null || storage == null || storage.HasDisposed)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            var resourcesToReserve = GetReservationOrder(action.Goal, storageAgent.Storage);
            if (resourcesToReserve.Count == 0)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            requestedTotal = resourcesToReserve.Sum(resource => resource.Amount);
            requestedSummary = string.Join(", ", resourcesToReserve.Select(resource => $"{resource.BlueprintId}:{resource.Amount}"));
            reservedTotal = 0;
            storageDescription = DescribeStorage(storage);
            usedStorageCount = 0;
            ClearTargetsQueue(action.Goal, outputQueueIndex);
            var usedStorages = new HashSet<IStorage>(ReferenceEqualityComparer<IStorage>.Instance);
            var minimumPriority = ZonePriority.None;
            var sourcePriority = ZonePriority.None;
            if (HaulingPriorityRules.TryGetGoalSourcePriority(action.Goal, creature, out sourcePriority))
            {
                minimumPriority = HaulingPriorityRules.GetRequiredMinimumPriority(sourcePriority, ZonePriority.None);
            }
            foreach (var resource in resourcesToReserve)
            {
                var reservedForResource = 0;
                var preferredOrder = StockpileDestinationPlanStore.TryGetActiveStorages(
                    action.Goal,
                    resource.BlueprintId,
                    out var plannedStorages)
                    ? plannedStorages
                    : null;
                var candidatePlan = StorageCandidatePlanner.BuildPlan(
                    action.Goal,
                    creature,
                    resource,
                    minimumPriority,
                    sourcePriority,
                    enablePriorityFallback: false,
                    Math.Max(1, resource.Amount),
                    preferredStorage: storage,
                    preferredOrder: preferredOrder,
                    exclude: null);

                foreach (var candidate in candidatePlan.Candidates)
                {
                    var currentStorage = candidate.Storage;
                    if (currentStorage == null || currentStorage.HasDisposed)
                    {
                        continue;
                    }

                    if (usedStorages.Add(currentStorage))
                    {
                        usedStorageCount = usedStorages.Count;
                        storageDescription = AppendStorageDescription(storageDescription, currentStorage);
                    }

                    while (reservedForResource < resource.Amount &&
                           currentStorage.ReserveStorage(resource, creature, out var storedAmount, out var position) &&
                           !currentStorage.HasDisposed)
                    {
                        if (storedAmount.Amount == 0)
                        {
                            break;
                        }

                        reservedForResource += storedAmount.Amount;
                        reservedTotal += storedAmount.Amount;
                        onStorageReserved?.Invoke(currentStorage, position);
                        QueueTarget(action.Goal, outputQueueIndex, new TargetObject(currentStorage, position));
                    }

                    if (reservedForResource >= resource.Amount)
                    {
                        break;
                    }
                }

                // The storage queue must follow the order of carried resources. If the current
                // resource only fits partially even after trying additional storages, later
                // resource types wait for the next search cycle.
                if (reservedForResource < resource.Amount)
                {
                    break;
                }
            }

            if (reservedTotal == 0)
            {
                if (jumpOnFail != null)
                {
                    JumpToAction(action.Goal, jumpOnFail);
                    return;
                }

                action.Complete(ActionCompletionStatus.Fail);
                return;
            }

            action.Complete(ActionCompletionStatus.Success);
        };
        action.OnComplete = status =>
        {
            var queueCount = action.Goal.GetTargetQueue(outputQueueIndex).Count;
            var targetStorage = action.Goal.GetTarget(storageIndex).ObjectInstance;
            DiagnosticTrace.Info(
                "haul.storage",
                () => $"ReserveAndQueue status={status}, goal={action.Goal.GetType().Name}, owner={action.AgentOwner}, storage={storageDescription}, target={targetStorage?.GetType().Name ?? "<none>"}, storagesUsed={usedStorageCount}, queuedSlots={queueCount}, requestedTotal={requestedTotal}, reservedTotal={reservedTotal}, requested=[{requestedSummary}], carried=[{CarrySummaryUtil.Summarize((action.AgentOwner as IStorageAgent)?.Storage)}]");
        };
        __result = action;
    }

    [HarmonyPatch(typeof(ResourceActions), nameof(ResourceActions.StoreResourceOnStockpile))]
    [HarmonyPostfix]
    private static void StoreResourceOnStockpilePostfix(ref GoapAction __result, TargetIndex target)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var action = new GoapAction("StoreResourceFromStorageOnStockpile");
        var carryBefore = 0;
        var carriedBeforeSummary = "<unknown>";
        action.OnInit = delegate
        {
            var storageAgent = action.AgentOwner as IStorageAgent;
            carryBefore = storageAgent?.Storage?.GetTotalStoredCount() ?? 0;
            carriedBeforeSummary = CarrySummaryUtil.Summarize(storageAgent?.Storage);

            var targetObject = action.Goal.GetTarget(target);
            if (storageAgent?.Storage == null)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            var singleResource = storageAgent.Storage.GetSingleResource();
            if (singleResource == null || singleResource.Amount <= 0)
            {
                action.Complete(ActionCompletionStatus.Error);
                return;
            }

            if (targetObject.ObjectInstance is StockpileInstance stockpileInstance)
            {
                var reachablePosition = targetObject.ReachablePosition;
                var existingPile = stockpileInstance.GetResourcePileGridPosition(reachablePosition);
                if (existingPile != null && existingPile.Blueprint == singleResource.Blueprint)
                {
                    storageAgent.Storage.TransferTo(existingPile.GetStorage(), singleResource.Blueprint, singleResource.Amount);
                    action.Complete(ActionCompletionStatus.Success);
                    return;
                }

                var amountToSpawn = Math.Min(singleResource.Amount, singleResource.Blueprint.StackingLimit);
                var pileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(
                    singleResource.Clone(amountToSpawn),
                    GridUtils.GetWorldPosition(reachablePosition));
                if (pileView == null)
                {
                    action.Complete(ActionCompletionStatus.Error);
                    return;
                }

                MonoSingleton<ResourcePileTracker>.Instance.OnNewPileSpawnedOnStockpile(singleResource.Blueprint, pileView.ResourcePileInstance);
                storageAgent.Storage.Consume(singleResource.Blueprint, amountToSpawn);
                action.Complete(ActionCompletionStatus.Success);
                return;
            }

            if (targetObject.ObjectInstance is ShelfComponentInstance shelfComponentInstance)
            {
                if (shelfComponentInstance.AllStorage.Count == 0)
                {
                    action.Complete(ActionCompletionStatus.Error);
                    return;
                }

                var remainingAmount = singleResource.Amount;
                foreach (var item in shelfComponentInstance.AllStorage)
                {
                    var storedAmount = item.StoreResourcePile((CreatureBase)storageAgent, singleResource.Blueprint, remainingAmount);
                    remainingAmount -= storedAmount;
                    if (remainingAmount <= 0)
                    {
                        action.Complete(ActionCompletionStatus.Success);
                        return;
                    }
                }

                action.Complete(ActionCompletionStatus.Fail);
                return;
            }

            action.Goal.EndGoalWith(GoalCondition.Error);
        };
        action.OnComplete = status =>
        {
            var storageAgent = action.AgentOwner as IStorageAgent;
            var remaining = storageAgent?.Storage?.GetTotalStoredCount() ?? 0;
            var carriedAfterSummary = CarrySummaryUtil.Summarize(storageAgent?.Storage);
            var targetObject = action.Goal.GetTarget(target).ObjectInstance;
            if (action.AgentOwner is CreatureBase creature && remaining == 0)
            {
                UnloadCarryContextStore.Clear(creature);
            }

            DiagnosticTrace.Info(
                "haul.store",
                () => $"StoreResource status={status}, goal={action.Goal.GetType().Name}, owner={action.AgentOwner}, target={targetObject?.GetType().Name ?? "<none>"}, carryBefore={carryBefore}, carryAfter={remaining}, carriedBefore=[{carriedBeforeSummary}], carriedAfter=[{carriedAfterSummary}]");
        };
        __result = action;
    }

    private static List<ResourceInstance> GetReservationOrder(Goal goal, Storage storage)
    {
        if (goal is HaulingBaseGoal && MixedCollectPlanStore.HasMixedPlan(goal))
        {
            return storage.GetResourcesWithoutLock()
                .Where(resource => resource != null && !resource.HasDisposed && resource.Amount > 0)
                .ToList();
        }

        var singleResource = storage.GetSingleResource();
        return singleResource == null ? new List<ResourceInstance>() : new List<ResourceInstance> { singleResource };
    }

    private static void ClearTargetsQueue(Goal goal, TargetIndex index)
    {
        GoalTargetQueueAccess.ClearTargetsQueue(goal, index);
    }

    private static void QueueTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        GoalTargetQueueAccess.QueueTarget(goal, index, target);
    }

    private static void JumpToAction(Goal goal, GoapAction action)
    {
        JumpToActionMethod.Invoke(goal, new object[] { action });
    }

    private static bool TryRecoverEmptyStorageHaul(StockpileHaulingGoal goal)
    {
        var queuedSources = goal.GetTargetQueue(TargetIndex.A);
        if (queuedSources == null || queuedSources.Count == 0)
        {
            return false;
        }

        if (!StorageEmptyRecoveryStore.TryConsumeRetry(goal))
        {
            return false;
        }

        ClearTargetsQueue(goal, TargetIndex.B);
        var actions = ActionsField.GetValue(goal) as IList;
        if (actions == null || actions.Count == 0 || actions[0] is not GoapAction restartAction)
        {
            return false;
        }

        DiagnosticTrace.Info(
            "haul.recover",
            () => $"Recovered empty stockpile haul for {goal.AgentOwner}: queuedSources={queuedSources.Count}, retry={StorageEmptyRecoveryStore.GetRetryCount(goal)}");
        JumpToAction(goal, restartAction);
        return true;
    }

    private static string AppendStorageDescription(string current, IStorage nextStorage)
    {
        var nextDescription = DescribeStorage(nextStorage);
        if (current == "<none>")
        {
            return nextDescription;
        }

        return current.Contains(nextDescription, StringComparison.Ordinal) ? current : $"{current} + {nextDescription}";
    }

    private static string DescribeStorage(IStorage? storage)
    {
        if (storage == null)
        {
            return "<none>";
        }

        return $"{storage.GetType().Name}(priority={storage.Priority}, pos={DescribePosition(storage)})";
    }

    private static string DescribePosition(object instance)
    {
        var method = AccessTools.Method(instance.GetType(), "GetPosition", Type.EmptyTypes);
        if (method == null)
        {
            return "n/a";
        }

        return method.Invoke(instance, null) switch
        {
            Vec3Int cell => $"({cell.x},{cell.y},{cell.z})",
            _ => "n/a"
        };
    }
}
