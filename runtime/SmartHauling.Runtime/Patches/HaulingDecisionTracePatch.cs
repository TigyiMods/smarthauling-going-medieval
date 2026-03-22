using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NSEipix;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.Goap.Actions;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Pathfinding;
using NSMedieval.State;
using NSMedieval.Types;
using UnityEngine;
using NSMedieval.Village.Map.Pathfinding;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch(typeof(StockpileHaulingGoal), "FindAndProcessTargets")]
internal static class HaulingDecisionTracePatch
{
    private const float SourceClusterExtent = 36f;
    private const float PatchSweepExtent = 24f;
    private const float PatchSweepLinkExtent = 10f;
    private const float MixedGroundHarvestExtent = 12f;
    private const int PatchSweepCountThreshold = 4;
    private const int PatchSweepAmountThreshold = 3;
    private const float MinimumDetourBudget = 12f;
    private const float MaximumDetourBudget = 48f;
    private const float DetourBudgetMultiplier = 0.65f;
    private const float MinimumSourceSliceWeightBudget = 32f;

    private static readonly System.Reflection.PropertyInfo AllPileInstancesProperty =
        AccessTools.Property(typeof(ResourcePileManager), "AllPileInstances")!;

    private static readonly System.Reflection.PropertyInfo CanBeStoredProperty =
        AccessTools.Property(typeof(ResourcePileHaulingManager), "CanBeStored")!;

    private static readonly System.Reflection.PropertyInfo PilesToReStoreProperty =
        AccessTools.Property(typeof(ResourcePileHaulingManager), "PilesToReStore")!;

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> TotalTargetedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("TotalTargetedCount");

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> MaxCarryAmountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("MaxCaryAmount");

    private static readonly System.Reflection.MethodInfo ClearTargetsQueueMethod =
        AccessTools.Method(typeof(Goal), "ClearTargetsQueue", new[] { typeof(TargetIndex) })!;

    private static readonly System.Reflection.MethodInfo QueueTargetMethod =
        AccessTools.Method(typeof(Goal), "QueueTarget", new[] { typeof(TargetIndex), typeof(TargetObject) })!;

    private static bool Prefix(StockpileHaulingGoal __instance, ref bool __result)
    {
        if (!RuntimeActivation.IsActive)
        {
            return true;
        }

        DiagnosticTrace.Info("haul.find.start", $"Start for {__instance.AgentOwner}", 40);
        __result = TryBuildHardPlan(__instance);
        return false;
    }

    internal static void RefreshCoordinatedTask(Goal goal)
    {
        StockpileTaskBoard.RefreshGoal(goal);
    }

    internal static void ReleaseCoordinatedTask(Goal goal)
    {
        StockpileTaskBoard.ReleaseGoal(goal);
    }

    internal static void MarkTaskFailed(ResourcePileInstance pile)
    {
        StockpileTaskBoard.MarkFailed(pile);
    }

    private static void Postfix(StockpileHaulingGoal __instance, ref bool __result)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        var haulQueue = __instance.GetTargetQueue(TargetIndex.A)
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .Where(pile => pile != null)
            .ToList();

        var storageQueue = __instance.GetTargetQueue(TargetIndex.B)
            .Select(target => target.ObjectInstance as IStorage)
            .Where(target => target != null)
            .ToList();

        var firstPile = haulQueue.FirstOrDefault();
        var resourceSummary = firstPile?.GetStoredResource();
        var firstStorage = storageQueue.FirstOrDefault();
        var effectiveSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(firstPile);
        if (__result && firstPile != null)
        {
            GoalSourcePriorityStore.Set(__instance, effectiveSourcePriority);
        }
        else
        {
            StockpileDestinationPlanStore.Clear(__instance);
        }

        if (__result && firstPile != null && __instance.AgentOwner is CreatureBase owner &&
            !ClusterOwnershipStore.CanUsePile(owner, firstPile))
        {
            __instance.GetTargetQueue(TargetIndex.A).Clear();
            __instance.GetTargetQueue(TargetIndex.B).Clear();
            DiagnosticTrace.Info(
                "haul.find.result",
                $"Rejected claimed haul for {__instance.AgentOwner}: {resourceSummary?.BlueprintId ?? "<none>"}",
                120);
            __result = false;
        }
        else if (__result && firstPile != null && firstStorage != null &&
            !HaulingPriorityRules.CanMoveToPriority(effectiveSourcePriority, firstStorage.Priority))
        {
            __instance.GetTargetQueue(TargetIndex.A).Clear();
            __instance.GetTargetQueue(TargetIndex.B).Clear();
            DiagnosticTrace.Info(
                "haul.find.result",
                $"Rejected haul for {__instance.AgentOwner}: {resourceSummary?.BlueprintId ?? "<none>"} sourcePriority={effectiveSourcePriority} targetPriority={firstStorage.Priority}",
                120);
            __result = false;
        }
        var decisionContext = BuildDecisionContext(__instance, firstPile, firstStorage);
        DiagnosticTrace.Info(
            "haul.find.result",
            $"Result={__result}, piles={haulQueue.Count}, first={resourceSummary?.BlueprintId ?? "<none>"}:{resourceSummary?.Amount ?? 0}, sourcePriority={effectiveSourcePriority}, targetPriority={firstStorage?.Priority.ToString() ?? "None"}, targeted={TotalTargetedCountRef(__instance)}, carry={MaxCarryAmountRef(__instance)}, storageTargets={storageQueue.Count}, {decisionContext}",
            120);
    }

    private static bool TryBuildHardPlan(StockpileHaulingGoal goal)
    {
        if (goal.AgentOwner is not CreatureBase creature || goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return false;
        }

        ResetGoalState(goal);

        var selectedPlan = SelectSeedPlan(goal, creature);
        if (selectedPlan == null)
        {
            DiagnosticTrace.Info("haul.plan", $"Hard planner found no viable stockpile haul for {goal.AgentOwner}", 120);
            return false;
        }

        selectedPlan.FirstPile.ReserveAll();
        if (!MonoSingleton<ReservationManager>.Instance.TryReserveObject(selectedPlan.FirstPile, goal.AgentOwner))
        {
            MonoSingleton<ReservationManager>.Instance.ReleaseAll(selectedPlan.FirstPile);
            StockpileTaskBoard.MarkFailed(selectedPlan.FirstPile);
            DiagnosticTrace.Info("haul.plan", $"Hard planner failed to reserve seed pile {selectedPlan.FirstPile.BlueprintId} for {goal.AgentOwner}", 120);
            return false;
        }

        QueueTarget(goal, TargetIndex.A, new TargetObject(selectedPlan.FirstPile));
        QueueTarget(goal, TargetIndex.B, new TargetObject(selectedPlan.PrimaryStorage));
        GoalSourcePriorityStore.Set(goal, selectedPlan.SourcePriority);

        MaxCarryAmountRef(goal) = selectedPlan.PickupBudget;
        TotalTargetedCountRef(goal) = 0;

        DiagnosticTrace.Info(
            "haul.plan",
            $"Hard planner selected {selectedPlan.FirstPile.BlueprintId}: estTotal={selectedPlan.EstimatedTotal}, estResources={selectedPlan.EstimatedResourceTypes}, score={selectedPlan.Score:0.0}, storage={selectedPlan.PrimaryStorage.GetType().Name}[prio={selectedPlan.PrimaryStorage.Priority}], requested={selectedPlan.RequestedAmount}, destinationBudget={selectedPlan.DestinationBudget}, pickupBudget={selectedPlan.PickupBudget}, candidates={selectedPlan.CandidatePlan.Summarize()}",
            120);

        var destinationOutcome = AugmentSameTypeCluster(
            goal,
            selectedPlan.FirstPile,
            selectedPlan.SourcePatchPiles,
            selectedPlan.PrimaryStorage,
            selectedPlan.CandidatePlan,
            selectedPlan.CandidatePlan.OrderedStorages,
            selectedPlan.DestinationBudget);
        if (!destinationOutcome.Success)
        {
            ResetGoalState(goal);
            StockpileTaskBoard.MarkFailed(selectedPlan.FirstPile);
            DiagnosticTrace.Info(
                "haul.plan",
                $"Hard planner failed to finalize destinations for {selectedPlan.FirstPile.BlueprintId}: {destinationOutcome.Summary}",
                120);
            return false;
        }

        if (destinationOutcome.LeasedAmount > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Leased destination capacity for {selectedPlan.FirstPile.BlueprintId}: leased={destinationOutcome.LeasedAmount}, storages={destinationOutcome.StorageCount}, summary={destinationOutcome.Summary}",
                80);
        }

        if (StockpileDestinationPlanStore.TryGet(goal, out var destinationPlan))
        {
            var plannedPickups = goal.GetTargetQueue(TargetIndex.A)
                .Select(target => target.GetObjectAs<ResourcePileInstance>())
                .Where(pile => pile != null)
                .Cast<ResourcePileInstance>()
                .ToList();
            CoordinatedStockpileTaskStore.Set(
                goal,
                MaxCarryAmountRef(goal),
                selectedPlan.SourcePriority,
                plannedPickups,
                destinationPlan);
        }

        return TotalTargetedCountRef(goal) > 0 &&
               goal.GetTargetQueue(TargetIndex.A).Count > 0 &&
               StockpileDestinationPlanStore.TryGet(goal, out _);
    }

    private static PlannedSeedSelection? SelectSeedPlan(StockpileHaulingGoal goal, CreatureBase creature)
    {
        return StockpileTaskBoard.TryAssign(goal, creature, out var claimed)
            ? claimed
            : null;
    }

    internal static List<StockpileTaskSeed> BuildTaskSnapshotForBoard()
    {
        var candidates = new List<StockpileTaskSeed>();
        var claimedSourcePatches = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in GetAllPileInstances()
                     .Where(pile => pile != null && !pile.HasDisposed)
                     .OrderByDescending(pile => pile.GetStoredResource()?.Amount ?? 0))
        {
            if (claimedSourcePatches.Contains(pile))
            {
                continue;
            }

            var selection = TryCreateTaskSeed(pile);
            if (selection == null)
            {
                continue;
            }

            candidates.Add(selection);
            foreach (var sourcePatchPile in selection.SourcePatchPiles)
            {
                claimedSourcePatches.Add(sourcePatchPile);
            }
        }

        return candidates
            .OrderByDescending(selection => selection.Score)
            .ToList();
    }

    internal static List<ResourcePileInstance> GetHaulablePileSnapshot()
    {
        return GetAllPileInstances();
    }

    internal static PlannedSeedSelection? TryMaterializeBoardSelection(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        StockpileTaskSeed taskSeed)
    {
        if (creature is not IStorageAgent { Storage: not null })
        {
            return null;
        }

        var firstPile = taskSeed.FirstPile;
        if (firstPile == null ||
            firstPile.HasDisposed ||
            HaulFailureBackoffStore.IsCoolingDown(firstPile) ||
            !ClusterOwnershipStore.CanUsePile(creature, firstPile) ||
            !CanReachPile(goal, firstPile) ||
            !ValidatePile(goal, firstPile))
        {
            return null;
        }

        var storedResource = firstPile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return null;
        }

        var requestedAmount = GetOptimisticPickupBudget(goal, storedResource.Blueprint);
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            taskSeed.SourcePriority,
            enablePriorityFallback: false,
            requestedAmount);

        var primaryStorage = candidatePlan.Primary?.Storage;
        if (primaryStorage == null)
        {
            return null;
        }

        var destinationBudget = candidatePlan.GetEstimatedCapacityBudget(requestedAmount);
        if (destinationBudget <= 0)
        {
            return null;
        }

        var pickupBudget = Math.Max(1, Mathf.Min(destinationBudget, requestedAmount));
        var score = HaulingScore.CalculateMaterializedSelectionScore(
            pickupBudget,
            requestedAmount,
            taskSeed.EstimatedPileCount,
            taskSeed.EstimatedResourceTypes);

        return new PlannedSeedSelection(
            firstPile,
            taskSeed.SourcePatchPiles,
            primaryStorage,
            taskSeed.SourcePriority,
            candidatePlan,
            requestedAmount,
            destinationBudget,
            pickupBudget,
            taskSeed.EstimatedTotal,
            taskSeed.EstimatedResourceTypes,
            taskSeed.EstimatedPileCount,
            score);
    }

    internal static float GetBoardClaimScore(CreatureBase creature, PlannedSeedSelection selection)
    {
        var distanceToSource = Vector3.Distance(creature.GetPosition(), selection.FirstPile.GetPosition());
        return HaulingScore.CalculateBoardAssignmentScore(selection.Score, distanceToSource);
    }

    private static StockpileTaskSeed? TryCreateTaskSeed(
        ResourcePileInstance firstPile)
    {
        if (firstPile == null ||
            firstPile.HasDisposed ||
            HaulFailureBackoffStore.IsCoolingDown(firstPile))
        {
            return null;
        }

        var storedResource = firstPile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return null;
        }

        var sameTypePiles = GetAllPileInstances()
            .Where(pile => !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();
        if (sameTypePiles.Count == 0)
        {
            return null;
        }

        var usePatchSweep = ShouldUsePatchSweep(firstPile, sameTypePiles);
        var fullSourcePatchPiles = usePatchSweep
            ? BuildPatchComponent(firstPile, sameTypePiles)
            : new HashSet<ResourcePileInstance>(
                sameTypePiles.Where(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()) <= SourceClusterExtent),
                ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        fullSourcePatchPiles.Add(firstPile);

        var sourcePatchPiles = BuildSourceSlice(firstPile, storedResource.Blueprint, fullSourcePatchPiles);

        var mixedPatchPiles = GetAllPileInstances()
            .Where(pile =>
                !pile.HasDisposed &&
                !sourcePatchPiles.Contains(pile) &&
                IsNearPlannedSourcePatch(sourcePatchPiles, pile))
            .ToList();

        var estimatedTotal = SumAmounts(sourcePatchPiles) + SumAmounts(mixedPatchPiles);
        if (estimatedTotal <= 0)
        {
            return null;
        }

        var estimatedPileCount = sourcePatchPiles.Count + mixedPatchPiles.Count;
        var estimatedResourceTypes = sourcePatchPiles
            .Concat(mixedPatchPiles)
            .Select(pile => pile.BlueprintId)
            .Distinct()
            .Count();
        var patchExtent = GetPatchExtent(firstPile, sourcePatchPiles.Concat(mixedPatchPiles));
        var score = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal,
            patchExtent,
            estimatedResourceTypes,
            estimatedPileCount,
            firstPile.BlueprintId.Contains("sapling", System.StringComparison.OrdinalIgnoreCase));

        return new StockpileTaskSeed(
            firstPile,
            sourcePatchPiles.ToList(),
            StoragePriorityUtil.GetEffectiveSourcePriority(firstPile),
            estimatedTotal,
            estimatedResourceTypes,
            estimatedPileCount,
            usePatchSweep,
            score);
    }

    private static int RetargetStorageAndGetBudget(StockpileHaulingGoal goal, ResourcePileInstance firstPile, ref IStorage? firstStorage)
    {
        if (goal.AgentOwner is not CreatureBase creature)
        {
            return MaxCarryAmountRef(goal);
        }

        var storedResource = firstPile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return MaxCarryAmountRef(goal);
        }

        var requestedAmount = GetOptimisticPickupBudget(goal, storedResource.Blueprint);
        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(firstPile);
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            sourcePriority,
            enablePriorityFallback: false,
            requestedAmount,
            preferredStorage: firstStorage);

        var bestCandidate = candidatePlan.Primary;
        if (bestCandidate == null)
        {
            StockpileDestinationPlanStore.Clear(goal);
            DiagnosticTrace.Info(
                "haul.plan",
                $"Destination plan empty for {storedResource.BlueprintId}: requested={requestedAmount}, sourcePriority={sourcePriority}, candidates={candidatePlan.Summarize()}",
                120);
            firstStorage = null;
            return 0;
        }

        StockpileDestinationPlanStore.Set(
            goal,
            candidatePlan.OrderedStorages,
            candidatePlan.RequestedAmount,
            storedResource.Blueprint.GetID());

        if (firstStorage == null || !ReferenceEquals(bestCandidate.Storage, firstStorage))
        {
            ClearTargetsQueue(goal, TargetIndex.B);
            QueueTarget(goal, TargetIndex.B, new TargetObject(bestCandidate.Storage));
            firstStorage = bestCandidate.Storage;
            DiagnosticTrace.Info(
                "haul.plan",
                $"Retargeted destination for {storedResource.BlueprintId}: storage={bestCandidate.Storage.GetType().Name}, priority={bestCandidate.Storage.Priority}, cap={bestCandidate.EstimatedCapacity}, dist={(bestCandidate.Distance >= float.MaxValue / 8f ? "n/a" : bestCandidate.Distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}, storagesPlanned={candidatePlan.OrderedStorages.Count}, candidates={candidatePlan.Summarize()}",
                120);
        }

        return candidatePlan.GetEstimatedCapacityBudget(requestedAmount);
    }

    private static DestinationPlanOutcome AugmentSameTypeCluster(
        StockpileHaulingGoal goal,
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sourcePatchPiles,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        int destinationCapacityBudget)
    {
        if (goal.AgentOwner is not CreatureBase creature || goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return DestinationPlanOutcome.Failed("missing-agent");
        }

        var queue = goal.GetTargetQueue(TargetIndex.A);
        var queuedPiles = queue
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .Where(pile => pile != null)
            .Cast<ResourcePileInstance>()
            .ToList();

        if (!queuedPiles.Contains(firstPile))
        {
            queuedPiles.Insert(0, firstPile);
        }

        var knownPiles = new HashSet<ResourcePileInstance>(queuedPiles);
        var candidatePiles = new List<ResourcePileInstance>();
        var sameTypeTotal = 0;
        var sameTypeAmount = 0;
        var sameTypeWithinBudget = 0;
        var sameTypeWithinBudgetAmount = 0;
        var claimedByOther = 0;
        var validateRejected = 0;
        var priorityRejected = 0;
        var storageRejected = 0;
        var detourRejected = 0;
        var reachRejected = 0;
        var cooldownRejected = 0;
        var detailSamples = new List<string>();
        var targetPosition = TryGetPosition(firstStorage);
        var sourceToTargetDistance = targetPosition.HasValue
            ? Vector3.Distance(firstPile.GetPosition(), targetPosition.Value)
            : -1f;
        var detourBudget = GetDetourBudget(sourceToTargetDistance);
        var sameTypePiles = sourcePatchPiles
            .Where(pile => pile != null && !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();
        var usePatchSweep = ShouldUsePatchSweep(firstPile, sameTypePiles);
        var patchComponent = usePatchSweep
            ? BuildPatchComponent(firstPile, sameTypePiles)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in sameTypePiles)
        {
            if (knownPiles.Contains(pile) ||
                pile.HasDisposed ||
                pile.Blueprint != firstPile.Blueprint)
            {
                continue;
            }

            var storedResource = pile.GetStoredResource();
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeTotal++;
                sameTypeAmount += storedResource.Amount;
            }

            if (!IsSweepCandidateWorthwhile(firstPile, pile, targetPosition, detourBudget, usePatchSweep, patchComponent, out var detourCost))
            {
                detourRejected++;
                CaptureDetail(
                    detailSamples,
                    usePatchSweep
                        ? $"{pile.BlueprintId}:patch({detourCost:0.0}>{PatchSweepExtent:0.0})"
                        : targetPosition.HasValue
                            ? $"{pile.BlueprintId}:detour({detourCost:0.0}>{detourBudget:0.0})"
                            : $"{pile.BlueprintId}:radius");
                continue;
            }

            sameTypeWithinBudget++;
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeWithinBudgetAmount += storedResource.Amount;
            }

            if (!ClusterOwnershipStore.CanUsePile(creature, pile))
            {
                claimedByOther++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:claimed");
                continue;
            }

            if (HaulFailureBackoffStore.IsCoolingDown(pile))
            {
                cooldownRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:cooldown");
                continue;
            }

            if (!CanReachPile(goal, pile))
            {
                reachRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:reach");
                continue;
            }

            if (!ValidatePile(goal, pile))
            {
                validateRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:validate");
                continue;
            }

            if (storedResource == null ||
                storedResource.HasDisposed)
            {
                continue;
            }

            var pileSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
            if (!HaulingPriorityRules.CanMoveToPriority(pileSourcePriority, firstStorage.Priority))
            {
                priorityRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:priority({pileSourcePriority}->{firstStorage.Priority})");
                continue;
            }

            if (!firstStorage.CanStore(storedResource, creature))
            {
                storageRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:store");
                continue;
            }

            candidatePiles.Add(pile);
        }

        var orderedCandidates = candidatePiles
            .OrderBy(pile => usePatchSweep ? Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()) : GetAdditionalDetour(firstPile, pile, targetPosition))
            .ThenBy(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()))
            .ToList();

        var optimisticPickupBudget = GetOptimisticPickupBudget(goal, firstPile.Blueprint);
        var pickupBudget = Math.Max(1, destinationCapacityBudget > 0 ? Mathf.Min(destinationCapacityBudget, optimisticPickupBudget) : optimisticPickupBudget);
        var totalPlanned = 0;
        var plannedWeight = 0f;
        var plannedAny = storageAgent.Storage.HasOneOrMoreResources();
        var requestedByResourceId = new Dictionary<string, int>();
        var plannedAmountsByPile = new Dictionary<ResourcePileInstance, int>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in queuedPiles)
        {
            var storedResource = pile.GetStoredResource();
            if (storedResource == null || storedResource.HasDisposed)
            {
                continue;
            }

            var projected = PickupPlanningUtil.GetProjectedCapacity(storageAgent.Storage, storedResource.Blueprint, plannedWeight, plannedAny);
            if (projected <= 0)
            {
                break;
            }

            projected = Mathf.Min(projected, storedResource.Amount, pickupBudget - totalPlanned);
            if (projected <= 0)
            {
                break;
            }

            totalPlanned += projected;
            plannedWeight += PickupPlanningUtil.GetProjectedWeight(storageAgent.Storage, storedResource.Blueprint, projected);
            plannedAny = true;
            var resourceId = storedResource.Blueprint.GetID();
            requestedByResourceId[resourceId] = requestedByResourceId.TryGetValue(resourceId, out var currentAmount)
                ? currentAmount + projected
                : projected;
            plannedAmountsByPile[pile] = projected;
        }
        var plannedPiles = new List<ResourcePileInstance>(queuedPiles);
        var sameTypeAdded = 0;
        foreach (var pile in orderedCandidates)
        {
            if (!TryPlanAdditionalPile(goal, queue, pile, storageAgent.Storage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
            {
                continue;
            }

            sameTypeAdded++;
            plannedPiles.Add(pile);
        }

        var mixedAdded = 0;
        var mixedDetails = new List<string>();
        if (totalPlanned < pickupBudget)
        {
            var plannedPileSet = new HashSet<ResourcePileInstance>(plannedPiles, ReferenceEqualityComparer<ResourcePileInstance>.Instance);
            var allPileInstances = AllPileInstancesProperty.GetValue(MonoSingleton<ResourcePileManager>.Instance) as IEnumerable;
            if (allPileInstances == null)
            {
                return DestinationPlanOutcome.Failed("missing-pile-source");
            }

            var mixedCandidates = new List<ResourcePileInstance>();
            foreach (var pileObject in allPileInstances)
            {
                if (pileObject is not ResourcePileInstance pile ||
                    plannedPileSet.Contains(pile) ||
                    pile.HasDisposed ||
                    !IsNearPlannedSourcePatch(plannedPiles, pile))
                {
                    continue;
                }

                mixedCandidates.Add(pile);
            }

            foreach (var pile in mixedCandidates
                         .OrderBy(candidate => GetNearestPatchDistance(plannedPiles, candidate))
                         .ThenBy(candidate => Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition())))
            {
                var storedResource = pile.GetStoredResource();
                if (storedResource == null || storedResource.HasDisposed)
                {
                    continue;
                }

                if (!CanReachPile(goal, pile))
                {
                    CaptureDetail(mixedDetails, $"{pile.BlueprintId}:reach");
                    continue;
                }

                if (!CanConsiderMixedPile(goal, creature, pile, firstStorage, preferredDestinationOrder, out var mixedRejection, out var compatibleStorage))
                {
                    CaptureDetail(mixedDetails, $"{pile.BlueprintId}:{mixedRejection}");
                    continue;
                }

                if (!TryPlanAdditionalPile(goal, queue, pile, storageAgent.Storage, ref plannedWeight, ref plannedAny, ref totalPlanned, pickupBudget, requestedByResourceId, plannedAmountsByPile))
                {
                    CaptureDetail(mixedDetails, $"{pile.BlueprintId}:capacity");
                    continue;
                }

                mixedAdded++;
                plannedPiles.Add(pile);
                CaptureDetail(mixedDetails, $"{pile.BlueprintId}@{compatibleStorage?.Priority.ToString() ?? "None"}");

                if (totalPlanned >= pickupBudget)
                {
                    break;
                }
            }
        }

        var claimed = ClusterOwnershipStore.ClaimCluster(goal, creature, plannedPiles);
        ClusterOwnershipStore.RefreshGoal(goal);

        if (claimed > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Claimed source cluster for {firstPile.BlueprintId}: mode={(usePatchSweep ? "patch" : "route")}, claimed={claimed}, queued={queuedPiles.Count}, routed={orderedCandidates.Count}, planned={plannedPiles.Count}, sameTypeGround={sameTypeTotal}:{sameTypeAmount}, withinBudget={sameTypeWithinBudget}:{sameTypeWithinBudgetAmount}, route[sourceToTarget={(sourceToTargetDistance >= 0f ? sourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, budget={detourBudget:0.0}], rejected[claimed={claimedByOther}, detour={detourRejected}, reach={reachRejected}, cooldown={cooldownRejected}, validate={validateRejected}, priority={priorityRejected}, store={storageRejected}], details=[{string.Join("; ", detailSamples)}], owner={goal.AgentOwner}",
                80);
        }

        var destinationOutcome = ApplyResourceDestinationPlans(
            goal,
            creature,
            firstPile,
            firstStorage,
            primaryCandidatePlan,
            preferredDestinationOrder,
            queue,
            plannedPiles,
            requestedByResourceId,
            plannedAmountsByPile);
        if (!destinationOutcome.Success)
        {
            return destinationOutcome;
        }

        totalPlanned = requestedByResourceId.Values.Sum();
        if (totalPlanned > 0)
        {
            TotalTargetedCountRef(goal) = totalPlanned;
            MaxCarryAmountRef(goal) = totalPlanned;
        }

        if (requestedByResourceId.Count > 1)
        {
            MixedCollectPlanStore.Set(goal, requestedByResourceId);
            DiagnosticTrace.Info(
                "haul.plan",
                $"Planned mixed ground harvest for {firstPile.BlueprintId}: resources=[{string.Join(", ", requestedByResourceId.Select(entry => $"{entry.Key}={entry.Value}"))}]",
                120);
        }
        else
        {
            MixedCollectPlanStore.Clear(goal);
        }

        DiagnosticTrace.Info(
            "haul.plan",
            $"Clustered source piles for {firstPile.BlueprintId}: mode={(usePatchSweep ? "patch" : "route")}, sameTypeAdded={sameTypeAdded}, mixedAdded={mixedAdded}, totalTargeted={totalPlanned}, pickupBudget={pickupBudget}, routeBudget={detourBudget:0.0}, sourceToTarget={(sourceToTargetDistance >= 0f ? sourceToTargetDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, destinations={destinationOutcome.Summary}, mixed=[{string.Join("; ", mixedDetails)}]",
            120);

        return destinationOutcome;
    }

    private static bool TryPlanAdditionalPile(
        Goal goal,
        List<TargetObject> queue,
        ResourcePileInstance pile,
        NSMedieval.Components.Storage storage,
        ref float plannedWeight,
        ref bool plannedAny,
        ref int totalPlanned,
        int pickupBudget,
        Dictionary<string, int> requestedByResourceId,
        Dictionary<ResourcePileInstance, int> plannedAmountsByPile)
    {
        var storedResource = pile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return false;
        }

        var projected = PickupPlanningUtil.GetProjectedCapacity(storage, storedResource.Blueprint, plannedWeight, plannedAny);
        if (projected <= 0)
        {
            return false;
        }

        projected = Mathf.Min(projected, storedResource.Amount, pickupBudget - totalPlanned);
        if (projected <= 0)
        {
            return false;
        }

        pile.ReserveAll();
        if (!MonoSingleton<ReservationManager>.Instance.TryReserveObject(pile, goal.AgentOwner))
        {
            MonoSingleton<ReservationManager>.Instance.ReleaseAll(pile);
            return false;
        }

        queue.Add(new TargetObject(pile));
        totalPlanned += projected;
        plannedWeight += PickupPlanningUtil.GetProjectedWeight(storage, storedResource.Blueprint, projected);
        plannedAny = true;
        var resourceId = storedResource.Blueprint.GetID();
        requestedByResourceId[resourceId] = requestedByResourceId.TryGetValue(resourceId, out var currentAmount)
            ? currentAmount + projected
            : projected;
        plannedAmountsByPile[pile] = projected;
        return true;
    }

    private static bool IsNearPlannedSourcePatch(IReadOnlyCollection<ResourcePileInstance> plannedPiles, ResourcePileInstance candidatePile)
    {
        return GetNearestPatchDistance(plannedPiles, candidatePile) <= MixedGroundHarvestExtent;
    }

    private static IReadOnlyList<ResourcePileInstance> BuildSourceSlice(
        ResourcePileInstance firstPile,
        Resource sampleResource,
        IReadOnlyCollection<ResourcePileInstance> fullSourcePatchPiles)
    {
        var sliceBudgetWeight = GetNominalSourceSliceWeightBudget(sampleResource);
        var orderedPiles = fullSourcePatchPiles
            .Where(pile => pile != null && !pile.HasDisposed)
            .OrderBy(pile => ReferenceEquals(pile, firstPile) ? 0 : 1)
            .ThenBy(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()))
            .ThenByDescending(pile => pile.GetStoredResource()?.Amount ?? 0)
            .ToList();

        var slice = new List<ResourcePileInstance>();
        var accumulatedWeight = 0f;
        foreach (var pile in orderedPiles)
        {
            var resource = pile.GetStoredResource();
            if (resource == null || resource.HasDisposed)
            {
                continue;
            }

            var pileWeight = GetNominalPileWeight(resource);
            if (pileWeight <= 0f)
            {
                continue;
            }

            if (slice.Count > 0 && accumulatedWeight >= sliceBudgetWeight)
            {
                break;
            }

            slice.Add(pile);
            var remainingWeight = Mathf.Max(0f, sliceBudgetWeight - accumulatedWeight);
            accumulatedWeight += slice.Count == 1
                ? Mathf.Min(pileWeight, sliceBudgetWeight)
                : Mathf.Min(pileWeight, Mathf.Max(0.01f, remainingWeight));
            if (accumulatedWeight >= sliceBudgetWeight)
            {
                break;
            }
        }

        if (slice.Count == 0)
        {
            slice.Add(firstPile);
        }

        return slice;
    }

    private static DestinationPlanOutcome ApplyResourceDestinationPlans(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance firstPile,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        Dictionary<string, int> requestedByResourceId,
        Dictionary<ResourcePileInstance, int> plannedAmountsByPile)
    {
        var primaryResourceId = firstPile.Blueprint.GetID();
        var prunedResources = new HashSet<string>();

        for (var pass = 0; pass < 3; pass++)
        {
            var build = BuildResourceDestinationPlans(
                goal,
                creature,
                firstStorage,
                preferredDestinationOrder,
                plannedPiles,
                requestedByResourceId,
                primaryResourceId);

            if (!build.UnsupportedResourceIds.Contains(primaryResourceId) &&
                build.ResourcePlans.Count > 0)
            {
                if (build.UnsupportedResourceIds.Count > 0)
                {
                    foreach (var unsupportedResourceId in build.UnsupportedResourceIds)
                    {
                        prunedResources.Add(unsupportedResourceId);
                    }

                    TrimPlannedPilesToRequestedAmounts(
                        goal,
                        queue,
                        plannedPiles,
                        requestedByResourceId,
                        plannedAmountsByPile,
                        build.RequestedAmountByResourceId);
                    continue;
                }

                CommitDestinationPlans(goal, build, primaryResourceId);
                var leasedAmount = DestinationLeaseStore.LeasePlans(goal, creature, build.CandidatePlans);
                return DestinationPlanOutcome.Succeeded(
                    leasedAmount,
                    build.ResourcePlans.SelectMany(plan => plan.OrderedStorages).Distinct(ReferenceEqualityComparer<IStorage>.Instance).Count(),
                    DescribeDestinationBuild(build, prunedResources));
            }

            break;
        }

        if (TryBuildPrimaryOnlyDestinationPlan(
                goal,
                creature,
                firstPile,
                firstStorage,
                primaryCandidatePlan,
                preferredDestinationOrder,
                queue,
                plannedPiles,
                requestedByResourceId,
                plannedAmountsByPile,
                out var primaryOnlyOutcome))
        {
            return primaryOnlyOutcome;
        }

        return DestinationPlanOutcome.Failed(
            $"primary={primaryResourceId}, requested=[{string.Join(", ", requestedByResourceId.Select(entry => $"{entry.Key}={entry.Value}"))}]");
    }

    private static ResourceDestinationBuild BuildResourceDestinationPlans(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        IStorage firstStorage,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        IReadOnlyCollection<ResourcePileInstance> plannedPiles,
        IReadOnlyDictionary<string, int> requestedByResourceId,
        string primaryResourceId)
    {
        var resourceGroups = plannedPiles
            .Where(pile => pile != null && !pile.HasDisposed)
            .GroupBy(pile => pile.Blueprint.GetID())
            .ToDictionary(
                group => group.Key,
                group => new PlannedResourceGroup(
                    group.Key,
                    group.ToList(),
                    group.Select(item => item.GetStoredResource()).FirstOrDefault(resource => resource != null && !resource.HasDisposed)!,
                    (ZonePriority)group.Max(item => (int)StoragePriorityUtil.GetEffectiveSourcePriority(item))));

        var orderedResourceIds = requestedByResourceId.Keys
            .OrderBy(resourceId => resourceId == primaryResourceId ? 0 : 1)
            .ThenByDescending(resourceId => requestedByResourceId[resourceId])
            .ToList();

        var resourcePlans = new List<StockpileDestinationResourcePlan>();
        var candidatePlans = new List<StorageCandidatePlanner.StorageCandidatePlan>();
        var requestedByResource = new Dictionary<string, int>();
        var unsupported = new HashSet<string>();
        var localReservedByStorage = new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);

        foreach (var resourceId in orderedResourceIds)
        {
            if (!resourceGroups.TryGetValue(resourceId, out var group) ||
                group.SampleResource == null ||
                group.SampleResource.HasDisposed ||
                !requestedByResourceId.TryGetValue(resourceId, out var requestedAmount) ||
                requestedAmount <= 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            var basePlan = StorageCandidatePlanner.BuildPlan(
                goal,
                creature,
                group.SampleResource,
                ZonePriority.None,
                group.SourcePriority,
                enablePriorityFallback: false,
                requestedAmount,
                preferredStorage: resourceId == primaryResourceId ? firstStorage : null,
                preferredOrder: preferredDestinationOrder);

            var adjustedCandidates = new List<StorageCandidatePlanner.StorageCandidate>();
            foreach (var candidate in basePlan.Candidates)
            {
                var locallyReserved = localReservedByStorage.TryGetValue(candidate.Storage, out var reserved) ? reserved : 0;
                var adjustedCapacity = Math.Max(0, candidate.EstimatedCapacity - locallyReserved);
                if (adjustedCapacity <= 0)
                {
                    continue;
                }

                adjustedCandidates.Add(new StorageCandidatePlanner.StorageCandidate(
                    candidate.Storage,
                    adjustedCapacity,
                    candidate.Distance,
                    candidate.FitRatio,
                    candidate.PriorityOvershoot,
                    candidate.PreferredOrderRank,
                    candidate.Position,
                    candidate.LeasedAmount + locallyReserved));
            }

            if (adjustedCandidates.Count == 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            var adjustedPlan = new StorageCandidatePlanner.StorageCandidatePlan(
                adjustedCandidates,
                basePlan.SourcePriority,
                basePlan.EffectiveMinimumPriority,
                requestedAmount);
            var plannedAmount = Math.Max(0, adjustedPlan.GetEstimatedCapacityBudget(requestedAmount));
            if (plannedAmount <= 0)
            {
                unsupported.Add(resourceId);
                continue;
            }

            requestedByResource[resourceId] = plannedAmount;
            resourcePlans.Add(new StockpileDestinationResourcePlan(resourceId, adjustedPlan.OrderedStorages, plannedAmount));
            candidatePlans.Add(new StorageCandidatePlanner.StorageCandidatePlan(
                adjustedCandidates,
                adjustedPlan.SourcePriority,
                adjustedPlan.EffectiveMinimumPriority,
                plannedAmount));

            var remaining = plannedAmount;
            foreach (var candidate in adjustedCandidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var reservedAmount = Mathf.Min(candidate.EstimatedCapacity, remaining);
                if (reservedAmount <= 0)
                {
                    continue;
                }

                localReservedByStorage[candidate.Storage] = localReservedByStorage.TryGetValue(candidate.Storage, out var current)
                    ? current + reservedAmount
                    : reservedAmount;
                remaining -= reservedAmount;
            }
        }

        return new ResourceDestinationBuild(resourcePlans, candidatePlans, unsupported, requestedByResource);
    }

    private static void CommitDestinationPlans(StockpileHaulingGoal goal, ResourceDestinationBuild build, string primaryResourceId)
    {
        StockpileDestinationPlanStore.Set(goal, primaryResourceId, build.ResourcePlans);

        if (build.ResourcePlans.FirstOrDefault(plan => plan.ResourceId == primaryResourceId)?.OrderedStorages.FirstOrDefault() is { } primaryStorage)
        {
            ClearTargetsQueue(goal, TargetIndex.B);
            QueueTarget(goal, TargetIndex.B, new TargetObject(primaryStorage));
        }
    }

    private static void TrimPlannedPilesToRequestedAmounts(
        Goal goal,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        IDictionary<string, int> requestedByResourceId,
        IReadOnlyDictionary<ResourcePileInstance, int> plannedAmountsByPile,
        IReadOnlyDictionary<string, int> allowedRequestedAmounts)
    {
        var remainingByResource = new Dictionary<string, int>(allowedRequestedAmounts);
        var keptPiles = new List<ResourcePileInstance>();
        var removedPiles = new List<ResourcePileInstance>();

        foreach (var pile in plannedPiles)
        {
            var resourceId = pile.Blueprint.GetID();
            if (!remainingByResource.TryGetValue(resourceId, out var remaining) || remaining <= 0)
            {
                removedPiles.Add(pile);
                continue;
            }

            keptPiles.Add(pile);
            var plannedAmount = plannedAmountsByPile.TryGetValue(pile, out var amount) ? amount : (pile.GetStoredResource()?.Amount ?? 0);
            remainingByResource[resourceId] = Mathf.Max(0, remaining - plannedAmount);
        }

        foreach (var removedPile in removedPiles)
        {
            MonoSingleton<ReservationManager>.Instance.ReleaseAll(removedPile);
        }

        queue.RemoveAll(target =>
        {
            var pile = target.GetObjectAs<ResourcePileInstance>();
            return pile != null && removedPiles.Contains(pile);
        });

        plannedPiles.Clear();
        plannedPiles.AddRange(keptPiles);

        requestedByResourceId.Clear();
        foreach (var entry in allowedRequestedAmounts)
        {
            if (entry.Value > 0)
            {
                requestedByResourceId[entry.Key] = entry.Value;
            }
        }
    }

    private static bool TryBuildPrimaryOnlyDestinationPlan(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance firstPile,
        IStorage firstStorage,
        StorageCandidatePlanner.StorageCandidatePlan primaryCandidatePlan,
        IReadOnlyList<IStorage> preferredDestinationOrder,
        List<TargetObject> queue,
        List<ResourcePileInstance> plannedPiles,
        IDictionary<string, int> requestedByResourceId,
        IReadOnlyDictionary<ResourcePileInstance, int> plannedAmountsByPile,
        out DestinationPlanOutcome outcome)
    {
        var primaryResourceId = firstPile.Blueprint.GetID();
        if (!requestedByResourceId.TryGetValue(primaryResourceId, out var requestedAmount) || requestedAmount <= 0)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-missing:{primaryResourceId}");
            return false;
        }

        var primaryResource = firstPile.GetStoredResource();
        if (primaryResource == null || primaryResource.HasDisposed)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-empty:{primaryResourceId}");
            return false;
        }

        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(firstPile);
        var reusableCandidates = primaryCandidatePlan.Candidates
            .Where(candidate =>
                candidate.Storage != null &&
                !candidate.Storage.HasDisposed &&
                candidate.EstimatedCapacity > 0 &&
                candidate.Storage.CanStore(primaryResource, creature) &&
                HaulingPriorityRules.CanMoveToPriority(sourcePriority, candidate.Storage.Priority))
            .ToList();

        var candidatePlan = reusableCandidates.Count > 0
            ? new StorageCandidatePlanner.StorageCandidatePlan(
                reusableCandidates,
                primaryCandidatePlan.SourcePriority,
                primaryCandidatePlan.EffectiveMinimumPriority,
                requestedAmount)
            : StorageCandidatePlanner.BuildPlan(
                goal,
                creature,
                primaryResource,
                ZonePriority.None,
                sourcePriority,
                enablePriorityFallback: false,
                requestedAmount,
                preferredStorage: firstStorage,
                preferredOrder: preferredDestinationOrder);

        if (candidatePlan.Primary == null)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-no-storage:{primaryResourceId}");
            return false;
        }

        var plannedAmount = Math.Max(0, candidatePlan.GetEstimatedCapacityBudget(requestedAmount));
        if (plannedAmount <= 0)
        {
            outcome = DestinationPlanOutcome.Failed($"primary-only-no-capacity:{primaryResourceId}");
            return false;
        }

        var requestedByPrimary = new Dictionary<string, int> { [primaryResourceId] = plannedAmount };
        TrimPlannedPilesToRequestedAmounts(goal, queue, plannedPiles, requestedByResourceId, plannedAmountsByPile, requestedByPrimary);

        var resourcePlans = new[]
        {
            new StockpileDestinationResourcePlan(primaryResourceId, candidatePlan.OrderedStorages, plannedAmount)
        };
        StockpileDestinationPlanStore.Set(goal, primaryResourceId, resourcePlans);
        ClearTargetsQueue(goal, TargetIndex.B);
        QueueTarget(goal, TargetIndex.B, new TargetObject(candidatePlan.Primary.Storage));
        var leasedAmount = DestinationLeaseStore.LeasePlan(goal, creature, candidatePlan.Candidates, plannedAmount);
        outcome = DestinationPlanOutcome.Succeeded(
            leasedAmount,
            candidatePlan.OrderedStorages.Count,
            $"fallback=primary-only:{primaryResourceId}:{plannedAmount}");
        return true;
    }

    private static string DescribeDestinationBuild(ResourceDestinationBuild build, IEnumerable<string> prunedResources)
    {
        var plans = string.Join(
            "; ",
            build.ResourcePlans.Select(plan => $"{plan.ResourceId}:{plan.RequestedAmount}->{plan.OrderedStorages.Count}"));
        var pruned = string.Join(", ", prunedResources.Distinct());
        return $"plans=[{plans}], pruned=[{(string.IsNullOrWhiteSpace(pruned) ? "<none>" : pruned)}]";
    }

    private static bool CanConsiderMixedPile(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance pile,
        IStorage primaryStorage,
        IReadOnlyCollection<IStorage> preferredOrder,
        out string rejection,
        out IStorage? compatibleStorage)
    {
        rejection = "unknown";
        compatibleStorage = null;

        if (!ClusterOwnershipStore.CanUsePile(creature, pile))
        {
            rejection = "claimed";
            return false;
        }

        if (HaulFailureBackoffStore.IsCoolingDown(pile))
        {
            rejection = "cooldown";
            return false;
        }

        var storedResource = pile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            rejection = "empty";
            return false;
        }

        var effectiveSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
        var requestedAmount = Math.Max(1, Math.Min(storedResource.Amount, GetOptimisticPickupBudget(goal, storedResource.Blueprint)));
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            effectiveSourcePriority,
            enablePriorityFallback: false,
            requestedAmount,
            preferredStorage: primaryStorage,
            preferredOrder: preferredOrder);

        compatibleStorage = candidatePlan.Primary?.Storage;
        if (compatibleStorage == null || candidatePlan.GetEstimatedCapacityBudget(requestedAmount) <= 0)
        {
            rejection = "dest";
            return false;
        }

        rejection = "ok";
        return true;
    }

    private static bool CanReachPile(StockpileHaulingGoal goal, ResourcePileInstance pile)
    {
        return HaulSourcePolicy.CanReachPile(goal, pile);
    }

    private static float GetNearestPatchDistance(IReadOnlyCollection<ResourcePileInstance> plannedPiles, ResourcePileInstance candidatePile)
    {
        var nearest = float.MaxValue;
        foreach (var plannedPile in plannedPiles)
        {
            var distance = Vector3.Distance(plannedPile.GetPosition(), candidatePile.GetPosition());
            if (distance < nearest)
            {
                nearest = distance;
            }
        }

        return nearest;
    }

    private static float GetPatchExtent(ResourcePileInstance firstPile, IEnumerable<ResourcePileInstance> piles)
    {
        var maxDistance = 0f;
        foreach (var pile in piles)
        {
            if (pile == null || pile.HasDisposed)
            {
                continue;
            }

            var distance = Vector3.Distance(firstPile.GetPosition(), pile.GetPosition());
            if (distance > maxDistance)
            {
                maxDistance = distance;
            }
        }

        return Mathf.Max(1f, maxDistance);
    }

    private static string BuildDecisionContext(StockpileHaulingGoal goal, ResourcePileInstance? firstPile, IStorage? firstStorage)
    {
        if (goal.AgentOwner is not CreatureBase creature || firstPile == null)
        {
            return "decision=<insufficient-context>";
        }

        var allPileInstances = GetAllPileInstances();
        var sameTypeAll = allPileInstances
            .Where(pile => !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();

        var sameTypeTargetable = sameTypeAll
            .Where(pile => ValidatePile(goal, pile))
            .ToList();
        var allTargetable = allPileInstances
            .Where(pile => !pile.HasDisposed && ValidatePile(goal, pile))
            .ToList();

        var agentPosition = creature.GetPosition();
        var sourcePosition = firstPile.GetPosition();
        var targetPosition = TryGetPosition(firstStorage);
        var agentToSource = Vector3.Distance(agentPosition, sourcePosition);
        var sourceToTarget = targetPosition.HasValue ? Vector3.Distance(sourcePosition, targetPosition.Value) : -1f;
        var agentToTarget = targetPosition.HasValue ? Vector3.Distance(agentPosition, targetPosition.Value) : -1f;
        var routeBudget = GetDetourBudget(sourceToTarget);
        var usePatchSweep = ShouldUsePatchSweep(firstPile, sameTypeAll);
        var patchComponent = usePatchSweep
            ? BuildPatchComponent(firstPile, sameTypeAll)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var sameTypeWithinBudget = sameTypeAll
            .Where(pile => IsSweepCandidateWorthwhile(firstPile, pile, targetPosition, routeBudget, usePatchSweep, patchComponent, out _))
            .ToList();
        var nearbyTargetable = allTargetable
            .Where(pile => Vector3.Distance(sourcePosition, pile.GetPosition()) <= Mathf.Max(PatchSweepExtent, MixedGroundHarvestExtent * 1.5f))
            .ToList();
        var agentStorage = (creature as IStorageAgent)?.Storage;
        var workerFreeSpace = agentStorage?.GetFreeSpace() ?? -1f;
        var workerIgnoreWeight = agentStorage != null && agentStorage.StorageBase.IgnoreWeigth;
        var firstResourceWeight = firstPile.GetStoredResource()?.Weight ?? 0f;

        return
            $"dist[a->s={agentToSource:0.0}, s->t={(sourceToTarget >= 0f ? sourceToTarget.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, a->t={(agentToTarget >= 0f ? agentToTarget.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}], pos[a={FormatPosition(agentPosition)}, s={FormatPosition(sourcePosition)}, t={FormatPosition(targetPosition)}], worker[free={FormatFloat(workerFreeSpace)}, ignoreWeight={workerIgnoreWeight}, firstWeight={firstResourceWeight:0.##}], sweepMode={(usePatchSweep ? "patch" : "route")}, route[budget={routeBudget:0.0}], sameType[all={sameTypeAll.Count}:{SumAmounts(sameTypeAll)}, targetable={sameTypeTargetable.Count}:{SumAmounts(sameTypeTargetable)}, worthwhile={sameTypeWithinBudget.Count}:{SumAmounts(sameTypeWithinBudget)}], allGroundTargetable={allTargetable.Count}, nearSource=[{SummarizePileGroups(nearbyTargetable)}], available=[{SummarizePileGroups(allTargetable)}]";
    }

    internal static string DescribeTaskSeeds(IEnumerable<StockpileTaskSeed> seeds, int maxSeeds = 6)
    {
        if (seeds == null)
        {
            return "<none>";
        }

        var summary = seeds
            .Where(seed => seed?.FirstPile != null)
            .Take(maxSeeds)
            .Select(seed => $"{seed!.FirstPile.BlueprintId}:{seed.EstimatedTotal}/{seed.EstimatedPileCount}p score={seed.Score:0.0}")
            .ToList();

        return summary.Count == 0 ? "<none>" : string.Join("; ", summary);
    }

    private static string SummarizePileGroups(IEnumerable<ResourcePileInstance> piles, int maxGroups = 6)
    {
        var summary = piles
            .Where(pile => pile != null && !pile.HasDisposed)
            .GroupBy(pile => pile.BlueprintId)
            .Select(group => new
            {
                BlueprintId = group.Key,
                Count = group.Count(),
                Amount = group.Sum(pile => pile.GetStoredResource()?.Amount ?? 0),
                Weight = group.Sum(pile =>
                {
                    var resource = pile.GetStoredResource();
                    if (resource == null || resource.HasDisposed)
                    {
                        return 0f;
                    }

                    var unitWeight = Mathf.Max(0.01f, resource.Weight);
                    return resource.Amount * unitWeight;
                })
            })
            .OrderByDescending(entry => entry.Weight)
            .ThenByDescending(entry => entry.Amount)
            .Take(maxGroups)
            .Select(entry => $"{entry.BlueprintId}:{entry.Count}p/{entry.Amount}u/{entry.Weight:0.#}w")
            .ToList();

        return summary.Count == 0 ? "<none>" : string.Join("; ", summary);
    }

    private static string FormatFloat(float value)
    {
        return value < 0f
            ? "n/a"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<ResourcePileInstance> GetAllPileInstances()
    {
        var haulingManager = MonoSingleton<ResourcePileHaulingManager>.Instance;
        var haulableSet = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        AddPileSequence(haulableSet, CanBeStoredProperty.GetValue(haulingManager) as IEnumerable);
        AddPileSequence(haulableSet, PilesToReStoreProperty.GetValue(haulingManager) as IEnumerable);
        if (haulableSet.Count > 0)
        {
            return haulableSet
                .Where(CanUseAsCentralHaulSource)
                .ToList();
        }

        var allPileInstances = AllPileInstancesProperty.GetValue(MonoSingleton<ResourcePileManager>.Instance) as IEnumerable;
        if (allPileInstances == null)
        {
            return new List<ResourcePileInstance>();
        }

        return allPileInstances
            .OfType<ResourcePileInstance>()
            .Where(CanUseAsCentralHaulSource)
            .ToList();
    }

    private static bool CanUseAsCentralHaulSource(ResourcePileInstance? pile)
    {
        return HaulSourcePolicy.CanUseAsCentralHaulSource(
            pile,
            StorageCandidatePlanner.GetAllStoragesSnapshot());
    }

    private static void AddPileSequence(HashSet<ResourcePileInstance> target, IEnumerable? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var pile in source.OfType<ResourcePileInstance>())
        {
            if (pile != null && !pile.HasDisposed)
            {
                target.Add(pile);
            }
        }
    }

    private static int SumAmounts(IEnumerable<ResourcePileInstance> piles)
    {
        return piles.Sum(pile => pile.GetStoredResource()?.Amount ?? 0);
    }

    private static void CaptureDetail(List<string> details, string value)
    {
        if (details.Count < 8)
        {
            details.Add(value);
        }
    }

    private static Vector3? TryGetPosition(object? instance)
    {
        if (instance == null)
        {
            return null;
        }

        var method = AccessTools.Method(instance.GetType(), "GetPosition", System.Type.EmptyTypes);
        if (method == null)
        {
            return null;
        }

        var result = method.Invoke(instance, null);
        return result switch
        {
            Vector3 vector => vector,
            _ => null
        };
    }

    private static string FormatPosition(Vector3? position)
    {
        if (!position.HasValue)
        {
            return "n/a";
        }

        return $"({position.Value.x:0.0},{position.Value.y:0.0},{position.Value.z:0.0})";
    }

    private static float GetDetourBudget(float sourceToTargetDistance)
    {
        if (sourceToTargetDistance <= 0f)
        {
            return SourceClusterExtent;
        }

        return Mathf.Clamp(sourceToTargetDistance * DetourBudgetMultiplier, MinimumDetourBudget, MaximumDetourBudget);
    }

    private static bool ShouldUsePatchSweep(ResourcePileInstance firstPile, IReadOnlyCollection<ResourcePileInstance> sameTypePiles)
    {
        if (firstPile.BlueprintId.Contains("sapling", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var storedResource = firstPile.GetStoredResource();
        return storedResource != null &&
               storedResource.Amount <= PatchSweepAmountThreshold &&
               sameTypePiles.Count >= PatchSweepCountThreshold;
    }

    private static HashSet<ResourcePileInstance> BuildPatchComponent(ResourcePileInstance firstPile, IReadOnlyCollection<ResourcePileInstance> sameTypePiles)
    {
        var component = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var frontier = new Queue<ResourcePileInstance>();
        component.Add(firstPile);
        frontier.Enqueue(firstPile);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var candidate in sameTypePiles)
            {
                if (component.Contains(candidate) ||
                    candidate.HasDisposed ||
                    Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition()) > PatchSweepExtent ||
                    Vector3.Distance(current.GetPosition(), candidate.GetPosition()) > PatchSweepLinkExtent)
                {
                    continue;
                }

                component.Add(candidate);
                frontier.Enqueue(candidate);
            }
        }

        return component;
    }

    private static bool IsSweepCandidateWorthwhile(
        ResourcePileInstance firstPile,
        ResourcePileInstance candidatePile,
        Vector3? targetPosition,
        float detourBudget,
        bool usePatchSweep,
        HashSet<ResourcePileInstance> patchComponent,
        out float detourCost)
    {
        if (usePatchSweep)
        {
            detourCost = Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition());
            return patchComponent.Contains(candidatePile);
        }

        return IsRouteWorthwhile(firstPile, candidatePile, targetPosition, detourBudget, out detourCost);
    }

    private static bool IsRouteWorthwhile(
        ResourcePileInstance firstPile,
        ResourcePileInstance candidatePile,
        Vector3? targetPosition,
        float detourBudget,
        out float detourCost)
    {
        if (!targetPosition.HasValue)
        {
            detourCost = Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition());
            return detourCost <= SourceClusterExtent;
        }

        detourCost = GetAdditionalDetour(firstPile, candidatePile, targetPosition);
        return detourCost <= detourBudget;
    }

    private static float GetAdditionalDetour(ResourcePileInstance firstPile, ResourcePileInstance candidatePile, Vector3? targetPosition)
    {
        if (!targetPosition.HasValue)
        {
            return Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition());
        }

        var direct = Vector3.Distance(firstPile.GetPosition(), targetPosition.Value);
        var viaCandidate = Vector3.Distance(firstPile.GetPosition(), candidatePile.GetPosition()) +
                           Vector3.Distance(candidatePile.GetPosition(), targetPosition.Value);
        return Mathf.Max(0f, viaCandidate - direct);
    }

    private static bool ValidatePile(HaulingBaseGoal goal, ResourcePileInstance pile)
    {
        return HaulSourcePolicy.ValidatePile(goal, pile);
    }

    private static int GetOptimisticPickupBudget(StockpileHaulingGoal goal, Resource blueprint)
    {
        if (goal.AgentOwner is IStorageAgent { Storage: not null } storageAgent && blueprint != null)
        {
            var projected = PickupPlanningUtil.GetProjectedCapacity(
                storageAgent.Storage,
                blueprint,
                0f,
                storageAgent.Storage.HasOneOrMoreResources());
            if (projected > 0)
            {
                return projected;
            }
        }

        return Math.Max(1, MaxCarryAmountRef(goal));
    }

    private static float GetNominalSourceSliceWeightBudget(Resource sampleResource)
    {
        var nominalWorkerFreeSpace = Mathf.Max(MinimumSourceSliceWeightBudget, StockpileTaskBoard.GetNominalWorkerFreeSpace());
        if (sampleResource == null)
        {
            return nominalWorkerFreeSpace;
        }

        if (sampleResource.EquipmentBlueprint != null ||
            (sampleResource.Category & (ResourceCategory.CtgCarcass | ResourceCategory.CtgStructure)) != ResourceCategory.None ||
            (sampleResource.Category == ResourceCategory.None && sampleResource.GetID().Contains("trophy")))
        {
            return Mathf.Max(1f, sampleResource.Weight);
        }

        return nominalWorkerFreeSpace;
    }

    private static float GetNominalPileWeight(ResourceInstance resource)
    {
        if (resource == null || resource.HasDisposed)
        {
            return 0f;
        }

        var blueprint = resource.Blueprint;
        if (blueprint == null)
        {
            return Mathf.Max(1f, resource.Amount);
        }

        if (blueprint.EquipmentBlueprint != null ||
            (blueprint.Category & (ResourceCategory.CtgCarcass | ResourceCategory.CtgStructure)) != ResourceCategory.None ||
            (blueprint.Category == ResourceCategory.None && blueprint.GetID().Contains("trophy")))
        {
            return Mathf.Max(1f, blueprint.Weight);
        }

        var unitWeight = Mathf.Max(0.01f, blueprint.Weight);
        return Mathf.Max(unitWeight, resource.Amount * unitWeight);
    }

    private static void ResetGoalState(StockpileHaulingGoal goal)
    {
        ReleaseQueuedTargets(goal, TargetIndex.A);
        ClearTargetsQueue(goal, TargetIndex.A);
        ClearTargetsQueue(goal, TargetIndex.B);
        MixedCollectPlanStore.Clear(goal);
        StockpileDestinationPlanStore.Clear(goal);
        CoordinatedStockpileTaskStore.Clear(goal);
        DestinationLeaseStore.ReleaseGoal(goal);
        GoalSourcePriorityStore.Clear(goal);
        ClusterOwnershipStore.ReleaseGoal(goal);
        TotalTargetedCountRef(goal) = 0;
        MaxCarryAmountRef(goal) = 0;
    }

    private static void ReleaseQueuedTargets(Goal goal, TargetIndex index)
    {
        foreach (var target in goal.GetTargetQueue(index))
        {
            if (target.ObjectInstance is IReservable reservable)
            {
                MonoSingleton<ReservationManager>.Instance.ReleaseAll(reservable);
            }
        }
    }

    private sealed class PlannedResourceGroup
    {
        public PlannedResourceGroup(string resourceId, IReadOnlyList<ResourcePileInstance> piles, ResourceInstance sampleResource, ZonePriority sourcePriority)
        {
            ResourceId = resourceId;
            Piles = piles;
            SampleResource = sampleResource;
            SourcePriority = sourcePriority;
        }

        public string ResourceId { get; }

        public IReadOnlyList<ResourcePileInstance> Piles { get; }

        public ResourceInstance SampleResource { get; }

        public ZonePriority SourcePriority { get; }
    }

    private sealed class ResourceDestinationBuild
    {
        public ResourceDestinationBuild(
            IReadOnlyList<StockpileDestinationResourcePlan> resourcePlans,
            IReadOnlyList<StorageCandidatePlanner.StorageCandidatePlan> candidatePlans,
            IReadOnlyCollection<string> unsupportedResourceIds,
            IReadOnlyDictionary<string, int> requestedAmountByResourceId)
        {
            ResourcePlans = resourcePlans;
            CandidatePlans = candidatePlans;
            UnsupportedResourceIds = unsupportedResourceIds;
            RequestedAmountByResourceId = requestedAmountByResourceId;
        }

        public IReadOnlyList<StockpileDestinationResourcePlan> ResourcePlans { get; }

        public IReadOnlyList<StorageCandidatePlanner.StorageCandidatePlan> CandidatePlans { get; }

        public IReadOnlyCollection<string> UnsupportedResourceIds { get; }

        public IReadOnlyDictionary<string, int> RequestedAmountByResourceId { get; }
    }

    private readonly struct DestinationPlanOutcome
    {
        private DestinationPlanOutcome(bool success, int leasedAmount, int storageCount, string summary)
        {
            Success = success;
            LeasedAmount = leasedAmount;
            StorageCount = storageCount;
            Summary = summary;
        }

        public bool Success { get; }

        public int LeasedAmount { get; }

        public int StorageCount { get; }

        public string Summary { get; }

        public static DestinationPlanOutcome Succeeded(int leasedAmount, int storageCount, string summary)
        {
            return new DestinationPlanOutcome(true, leasedAmount, storageCount, summary);
        }

        public static DestinationPlanOutcome Failed(string summary)
        {
            return new DestinationPlanOutcome(false, 0, 0, summary);
        }
    }

    internal sealed class PlannedSeedSelection
    {
        public PlannedSeedSelection(
            ResourcePileInstance firstPile,
            IReadOnlyList<ResourcePileInstance> sourcePatchPiles,
            IStorage primaryStorage,
            ZonePriority sourcePriority,
            StorageCandidatePlanner.StorageCandidatePlan candidatePlan,
            int requestedAmount,
            int destinationBudget,
            int pickupBudget,
            int estimatedTotal,
            int estimatedResourceTypes,
            int estimatedPileCount,
            float score)
        {
            FirstPile = firstPile;
            SourcePatchPiles = sourcePatchPiles;
            PrimaryStorage = primaryStorage;
            SourcePriority = sourcePriority;
            CandidatePlan = candidatePlan;
            RequestedAmount = requestedAmount;
            DestinationBudget = destinationBudget;
            PickupBudget = pickupBudget;
            EstimatedTotal = estimatedTotal;
            EstimatedResourceTypes = estimatedResourceTypes;
            EstimatedPileCount = estimatedPileCount;
            Score = score;
        }

        public ResourcePileInstance FirstPile { get; }

        public IReadOnlyList<ResourcePileInstance> SourcePatchPiles { get; }

        public IStorage PrimaryStorage { get; }

        public ZonePriority SourcePriority { get; }

        public StorageCandidatePlanner.StorageCandidatePlan CandidatePlan { get; }

        public int RequestedAmount { get; }

        public int DestinationBudget { get; }

        public int PickupBudget { get; }

        public int EstimatedTotal { get; }

        public int EstimatedResourceTypes { get; }

        public int EstimatedPileCount { get; }

        public float Score { get; }
    }

    private static void ClearTargetsQueue(Goal goal, TargetIndex index)
    {
        ClearTargetsQueueMethod.Invoke(goal, new object[] { index });
    }

    private static void QueueTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        QueueTargetMethod.Invoke(goal, new object[] { index, target });
    }
}
