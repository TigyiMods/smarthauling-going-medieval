using HarmonyLib;
using NSEipix;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

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
        if (!RuntimeServices.Reservations.TryReserveObject(selectedPlan.FirstPile, goal.AgentOwner))
        {
            RuntimeServices.Reservations.ReleaseAll(selectedPlan.FirstPile);
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

        var clusterAugment = StockpileClusterAugmentor.Apply(
            goal,
            selectedPlan.FirstPile,
            selectedPlan.SourcePatchPiles,
            selectedPlan.PrimaryStorage,
            selectedPlan.CandidatePlan,
            selectedPlan.CandidatePlan.OrderedStorages,
            selectedPlan.DestinationBudget,
            SourceClusterExtent,
            PatchSweepExtent,
            PatchSweepLinkExtent,
            MixedGroundHarvestExtent,
            PatchSweepCountThreshold,
            PatchSweepAmountThreshold,
            MinimumDetourBudget,
            MaximumDetourBudget,
            DetourBudgetMultiplier,
            GetOptimisticPickupBudget,
            ClearTargetsQueue,
            QueueTarget);
        if (!clusterAugment.DestinationOutcome.Success)
        {
            ResetGoalState(goal);
            StockpileTaskBoard.MarkFailed(selectedPlan.FirstPile);
            DiagnosticTrace.Info(
                "haul.plan",
                $"Hard planner failed to finalize destinations for {selectedPlan.FirstPile.BlueprintId}: {clusterAugment.DestinationOutcome.Summary}",
                120);
            return false;
        }

        if (clusterAugment.DestinationOutcome.LeasedAmount > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                $"Leased destination capacity for {selectedPlan.FirstPile.BlueprintId}: leased={clusterAugment.DestinationOutcome.LeasedAmount}, storages={clusterAugment.DestinationOutcome.StorageCount}, summary={clusterAugment.DestinationOutcome.Summary}",
                80);
        }

        if (clusterAugment.TotalPlanned > 0)
        {
            TotalTargetedCountRef(goal) = clusterAugment.TotalPlanned;
            MaxCarryAmountRef(goal) = clusterAugment.TotalPlanned;
        }

        if (clusterAugment.RequestedByResourceId.Count > 1)
        {
            MixedCollectPlanStore.Set(goal, clusterAugment.RequestedByResourceId.ToDictionary(entry => entry.Key, entry => entry.Value));
        }
        else
        {
            MixedCollectPlanStore.Clear(goal);
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
            !HaulSourcePolicy.CanReachPile(goal, firstPile) ||
            !HaulSourcePolicy.ValidatePile(goal, firstPile))
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
        return StockpileTaskSeedFactory.TryCreate(
            firstPile,
            GetAllPileInstances(),
            SourceClusterExtent,
            MixedGroundHarvestExtent,
            PatchSweepAmountThreshold,
            PatchSweepCountThreshold,
            PatchSweepExtent,
            PatchSweepLinkExtent,
            MinimumSourceSliceWeightBudget,
            StockpileTaskBoard.GetNominalWorkerFreeSpace());
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

    private static float GetPatchExtent(ResourcePileInstance firstPile, IEnumerable<ResourcePileInstance> piles)
    {
        return HaulGeometry.GetPatchExtent(
            firstPile.GetPosition(),
            piles.Where(pile => pile != null && !pile.HasDisposed)
                .Select(pile => pile.GetPosition()));
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
            .Where(pile => HaulSourcePolicy.ValidatePile(goal, pile))
            .ToList();
        var allTargetable = allPileInstances
            .Where(pile => !pile.HasDisposed && HaulSourcePolicy.ValidatePile(goal, pile))
            .ToList();

        var agentPosition = creature.GetPosition();
        var sourcePosition = firstPile.GetPosition();
        var targetPosition = StockpileClusterAugmentor.TryGetPosition(firstStorage);
        var agentToSource = Vector3.Distance(agentPosition, sourcePosition);
        var sourceToTarget = targetPosition.HasValue ? Vector3.Distance(sourcePosition, targetPosition.Value) : -1f;
        var agentToTarget = targetPosition.HasValue ? Vector3.Distance(agentPosition, targetPosition.Value) : -1f;
        var routeBudget = StockpileClusterAugmentor.GetDetourBudget(
            sourceToTarget,
            SourceClusterExtent,
            DetourBudgetMultiplier,
            MinimumDetourBudget,
            MaximumDetourBudget);
        var usePatchSweep = StockpileClusterAugmentor.ShouldUsePatchSweep(
            firstPile,
            sameTypeAll,
            PatchSweepAmountThreshold,
            PatchSweepCountThreshold);
        var patchComponent = usePatchSweep
            ? StockpileClusterAugmentor.BuildPatchComponent(firstPile, sameTypeAll, PatchSweepExtent, PatchSweepLinkExtent)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var sameTypeWithinBudget = sameTypeAll
            .Where(pile => StockpileClusterAugmentor.IsSweepCandidateWorthwhile(
                firstPile,
                pile,
                targetPosition,
                routeBudget,
                usePatchSweep,
                patchComponent,
                SourceClusterExtent,
                out _))
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
        return RuntimeServices.WorldSnapshot.GetCentralHaulSourcePiles().ToList();
    }

    private static int SumAmounts(IEnumerable<ResourcePileInstance> piles)
    {
        return piles.Sum(pile => pile.GetStoredResource()?.Amount ?? 0);
    }

    private static string FormatPosition(Vector3? position)
    {
        if (!position.HasValue)
        {
            return "n/a";
        }

        return $"({position.Value.x:0.0},{position.Value.y:0.0},{position.Value.z:0.0})";
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
                RuntimeServices.Reservations.ReleaseAll(reservable);
            }
        }
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
