using HarmonyLib;
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
    private static bool Prefix(StockpileHaulingGoal __instance, ref bool __result)
    {
        if (!RuntimeActivation.IsActive)
        {
            return true;
        }

        var plannerMode = DeterminePlannerMode(__instance);
        if (!plannerMode.UseCoordinatedPlanner)
        {
            DiagnosticTrace.Info(
                "haul.origin",
                () => $"Using vanilla stockpile planner for {__instance.AgentOwner}: reason={plannerMode.Reason}, recent={DescribeRecentGoal(__instance.AgentOwner as CreatureBase)}",
                200);
            return true;
        }

        DiagnosticTrace.Info(
            "haul.origin",
            () => $"Using smart stockpile planner for {__instance.AgentOwner}: reason={plannerMode.Reason}, recent={DescribeRecentGoal(__instance.AgentOwner as CreatureBase)}",
            200);
        DiagnosticTrace.Info("haul.find.start", () => $"Start for {__instance.AgentOwner}", 40);
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

        var creature = __instance.AgentOwner as CreatureBase;
        var isSmart = CoordinatedStockpileTaskStore.TryGet(__instance, out _);
        var observed = ObservePlanState(__instance, creature);
        var carryAtStart = __instance.AgentOwner is IStorageAgent { Storage: not null } currentStorageAgent
            ? currentStorageAgent.Storage.GetTotalStoredCount()
            : 0;
        RecentGoalOriginStore.RecentGoalEndContext? recentGoal = null;
        if (creature != null && RecentGoalOriginStore.TryGetRecent(creature, out var recent))
        {
            recentGoal = recent;
        }

        PlayerForcedHaulIntentStore.PendingIntent? playerForcedIntent = null;
        if (creature != null && PlayerForcedHaulIntentStore.TryPeek(creature, out var forcedIntent))
        {
            playerForcedIntent = forcedIntent;
        }

        var isUrgentPriorityHaul = __instance is StockpileUrgentHaulingGoal && !playerForcedIntent.HasValue;
        var effectivePlayerForcedIntent = ManualHaulIntentResolver.ResolveEffectiveIntent(
            __instance,
            creature,
            playerForcedIntent,
            observed.FirstPile);

        var playerForcedSourceMatches = effectivePlayerForcedIntent.HasValue &&
                                        effectivePlayerForcedIntent.Value.ContainsPriorityPile(observed.FirstPile);
        var playerForcedAnchorToSourceDistance = effectivePlayerForcedIntent.HasValue && observed.FirstPile != null
            ? Vector3.Distance(effectivePlayerForcedIntent.Value.AnchorPosition, observed.FirstPile.GetPosition())
            : -1f;

        var provenance = StockpileHaulOriginClassifier.Classify(
            effectivePlayerForcedIntent,
            playerForcedSourceMatches,
            playerForcedAnchorToSourceDistance,
            isUrgentPriorityHaul,
            recentGoal,
            observed.AgentToSource,
            carryAtStart);
        if (creature != null &&
            playerForcedIntent.HasValue &&
            provenance.Category == StockpileHaulOriginCategory.PlayerForced)
        {
            PlayerForcedHaulIntentStore.Clear(creature);
        }

        var takeoverCarryDecision = SmartTakeoverCarryGuard.Evaluate(recentGoal, carryAtStart);

        if (!isSmart && takeoverCarryDecision.ShouldBlock)
        {
            DiagnosticTrace.Info(
                "haul.takeover",
                () => $"Skipped smart takeover for {__instance.AgentOwner}: reason={takeoverCarryDecision.Reason}, recent={DescribeRecentGoal(creature)}",
                120);
        }
        else if (!isSmart)
        {
            if (provenance.Category == StockpileHaulOriginCategory.PlayerForced)
            {
                var allowUrgentAnchorOverride = isUrgentPriorityHaul && effectivePlayerForcedIntent.HasValue;
                if ((__result && observed.FirstPile != null) || allowUrgentAnchorOverride)
                {
                    isSmart = TryUpgradePlayerForcedPlan(
                        __instance,
                        creature,
                        observed.FirstPile,
                        observed.FirstStorage,
                        effectivePlayerForcedIntent,
                        preserveVanillaAnchor: !allowUrgentAnchorOverride,
                        ref __result,
                        ref observed);
                }
            }
            else if (__result && observed.FirstPile != null && provenance.Category == StockpileHaulOriginCategory.AutonomousHaul)
            {
                isSmart = TryUpgradeAutonomousPlan(
                    __instance,
                    creature,
                    ref __result,
                    ref observed);
            }
        }

        if (isSmart && __result && observed.FirstPile != null)
        {
            GoalSourcePriorityStore.Set(__instance, observed.EffectiveSourcePriority);
        }
        else if (isSmart)
        {
            StockpileDestinationPlanStore.Clear(__instance);
        }

        ApplySmartPlanGuards(__instance, ref __result, isSmart, observed);

        var resultValue = __result;
        var decisionContext = HaulingDecisionTraceDiagnostics.BuildDecisionContext(__instance, observed.FirstPile, observed.FirstStorage);
        DiagnosticTrace.Info(
            "haul.classify",
            () => $"category={provenance.Category}, reason={provenance.Reason}, recentClass={provenance.RecentGoalClass}, mode={(isSmart ? "smart" : "vanilla")}, owner={__instance.AgentOwner}, first={observed.ResourceSummary?.BlueprintId ?? "<none>"}:{observed.ResourceSummary?.Amount ?? 0}, a->s={(observed.AgentToSource >= 0f ? observed.AgentToSource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, playerForced={ManualHaulIntentResolver.DescribeIntent(effectivePlayerForcedIntent, observed.FirstPile)}, recent={DescribeRecentGoal(creature)}",
            200);
        DiagnosticTrace.Info(
            isSmart ? "haul.find.result" : "haul.origin",
            () => $"mode={(isSmart ? "smart" : "vanilla")}, result={resultValue}, owner={__instance.AgentOwner}, piles={observed.HaulQueue.Count}, first={observed.ResourceSummary?.BlueprintId ?? "<none>"}:{observed.ResourceSummary?.Amount ?? 0}, sourcePriority={observed.EffectiveSourcePriority}, targetPriority={observed.FirstStorage?.Priority.ToString() ?? "None"}, targeted={StockpileHaulingGoalState.GetTotalTargetedCount(__instance)}, carry={StockpileHaulingGoalState.GetMaxCarryAmount(__instance)}, storageTargets={observed.StorageQueue.Count}, {decisionContext}, recent={DescribeRecentGoal(__instance.AgentOwner as CreatureBase)}",
            isSmart ? 120 : 200);
    }

    private static ObservedHaulPlanState ObservePlanState(StockpileHaulingGoal goal, CreatureBase? creature)
    {
        var haulQueue = goal.GetTargetQueue(TargetIndex.A)
            .Select(target => target.GetObjectAs<ResourcePileInstance>())
            .Where(pile => pile != null)
            .ToList();

        var storageQueue = goal.GetTargetQueue(TargetIndex.B)
            .Select(target => target.ObjectInstance as IStorage)
            .Where(target => target != null)
            .Cast<IStorage>()
            .ToList();

        var firstPile = haulQueue.FirstOrDefault();
        var firstStorage = storageQueue.FirstOrDefault();
        return new ObservedHaulPlanState(
            haulQueue,
            storageQueue,
            firstPile,
            firstPile?.GetStoredResource(),
            firstStorage,
            StoragePriorityUtil.GetEffectiveSourcePriority(firstPile),
            creature != null && firstPile != null
                ? Vector3.Distance(creature.GetPosition(), firstPile.GetPosition())
                : -1f);
    }

    private static bool TryUpgradePlayerForcedPlan(
        StockpileHaulingGoal goal,
        CreatureBase? creature,
        ResourcePileInstance? originalFirstPile,
        IStorage? originalFirstStorage,
        PlayerForcedHaulIntentStore.PendingIntent? playerForcedIntent,
        bool preserveVanillaAnchor,
        ref bool result,
        ref ObservedHaulPlanState observed)
    {
        var vanillaSnapshot = StockpileHaulingGoalState.CaptureVanillaPlan(goal, result);
        var anchorPile = preserveVanillaAnchor
            ? originalFirstPile
            : playerForcedIntent?.AnchorPile ?? originalFirstPile;
        var preferredStorage = preserveVanillaAnchor
            ? originalFirstStorage
            : ManualHaulIntentResolver.ResolvePreferredStorageForAnchor(creature, originalFirstStorage, anchorPile);
        if (anchorPile == null ||
            !TryBuildPlayerForcedPlan(goal, anchorPile, preferredStorage, playerForcedIntent))
        {
            StockpileHaulingGoalState.RestoreVanillaPlan(goal, vanillaSnapshot);
            result = vanillaSnapshot.Result;
            DiagnosticTrace.Info(
                "haul.takeover",
                () => $"Player-forced smart extension failed for {goal.AgentOwner}; restored vanilla plan {vanillaSnapshot.FirstBlueprintId}",
                120);
            return false;
        }

        var upgraded = ObservePlanState(goal, creature);
        var expectedFirstPile = preserveVanillaAnchor
            ? originalFirstPile
            : anchorPile;
        var expectedBlueprintId = expectedFirstPile?.BlueprintId ?? "<none>";
        if (expectedFirstPile == null ||
            upgraded.FirstPile == null ||
            !ReferenceEquals(upgraded.FirstPile, expectedFirstPile))
        {
            StockpileHaulingGoalState.RestoreVanillaPlan(goal, vanillaSnapshot);
            result = vanillaSnapshot.Result;
            DiagnosticTrace.Info(
                "haul.takeover",
                () => $"Rejected player-forced smart extension for {goal.AgentOwner}: expected={expectedBlueprintId}, planned={upgraded.FirstPile?.BlueprintId ?? "<none>"}",
                120);
            return false;
        }

        observed = upgraded;
        var plannedFirstBlueprintId = observed.ResourceSummary?.BlueprintId ?? "<none>";
        DiagnosticTrace.Info(
            "haul.takeover",
            () => $"Extended player-forced haul with local smart pickup for {goal.AgentOwner}: anchor={playerForcedIntent?.AnchorBlueprintId ?? vanillaSnapshot.FirstBlueprintId}, vanillaFirst={originalFirstPile?.BlueprintId ?? "<none>"}, plannedFirst={plannedFirstBlueprintId}, recent={DescribeRecentGoal(creature)}",
            120);
        return true;
    }

    private static bool TryUpgradeAutonomousPlan(
        StockpileHaulingGoal goal,
        CreatureBase? creature,
        ref bool result,
        ref ObservedHaulPlanState observed)
    {
        var vanillaSnapshot = StockpileHaulingGoalState.CaptureVanillaPlan(goal, result);
        if (!TryBuildHardPlan(goal))
        {
            StockpileHaulingGoalState.RestoreVanillaPlan(goal, vanillaSnapshot);
            result = vanillaSnapshot.Result;
            DiagnosticTrace.Info(
                "haul.takeover",
                () => $"Smart takeover failed for autonomous haul on {goal.AgentOwner}; restored vanilla plan {vanillaSnapshot.FirstBlueprintId}",
                120);
            return false;
        }

        observed = ObservePlanState(goal, creature);
        var smartFirstBlueprintId = observed.ResourceSummary?.BlueprintId ?? "<none>";
        DiagnosticTrace.Info(
            "haul.takeover",
            () => $"Upgraded autonomous haul to smart for {goal.AgentOwner}: vanillaFirst={vanillaSnapshot.FirstBlueprintId}, smartFirst={smartFirstBlueprintId}, recent={DescribeRecentGoal(creature)}",
            120);
        return true;
    }

    private static void ApplySmartPlanGuards(
        StockpileHaulingGoal goal,
        ref bool result,
        bool isSmart,
        ObservedHaulPlanState observed)
    {
        if (!isSmart || !result || observed.FirstPile == null)
        {
            return;
        }

        if (goal.AgentOwner is CreatureBase owner &&
            !ClusterOwnershipStore.CanUsePile(owner, observed.FirstPile))
        {
            goal.GetTargetQueue(TargetIndex.A).Clear();
            goal.GetTargetQueue(TargetIndex.B).Clear();
            DiagnosticTrace.Info(
                "haul.find.result",
                () => $"Rejected claimed haul for {goal.AgentOwner}: {observed.ResourceSummary?.BlueprintId ?? "<none>"}",
                120);
            result = false;
            return;
        }

        if (observed.FirstStorage != null &&
            !HaulingPriorityRules.CanMoveToPriority(observed.EffectiveSourcePriority, observed.FirstStorage.Priority))
        {
            goal.GetTargetQueue(TargetIndex.A).Clear();
            goal.GetTargetQueue(TargetIndex.B).Clear();
            DiagnosticTrace.Info(
                "haul.find.result",
                () => $"Rejected haul for {goal.AgentOwner}: {observed.ResourceSummary?.BlueprintId ?? "<none>"} sourcePriority={observed.EffectiveSourcePriority} targetPriority={observed.FirstStorage.Priority}",
                120);
            result = false;
        }
    }

    private static bool TryBuildHardPlan(StockpileHaulingGoal goal)
    {
        if (goal.AgentOwner is not CreatureBase creature || goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return false;
        }

        StockpileHaulingGoalState.ResetGoalState(goal);

        var selectedPlan = SelectSeedPlan(goal, creature);
        if (selectedPlan == null)
        {
            DiagnosticTrace.Info("haul.plan", () => $"Hard planner found no viable stockpile haul for {goal.AgentOwner}", 120);
            return false;
        }

        selectedPlan.FirstPile.ReserveAll();
        if (!RuntimeServices.Reservations.TryReserveObject(selectedPlan.FirstPile, goal.AgentOwner))
        {
            RuntimeServices.Reservations.ReleaseAll(selectedPlan.FirstPile);
            StockpileTaskBoard.MarkFailed(selectedPlan.FirstPile);
            DiagnosticTrace.Info("haul.plan", () => $"Hard planner failed to reserve seed pile {selectedPlan.FirstPile.BlueprintId} for {goal.AgentOwner}", 120);
            return false;
        }

        StockpileHaulingGoalState.SeedInitialTargets(
            goal,
            selectedPlan.FirstPile,
            selectedPlan.PrimaryStorage,
            selectedPlan.SourcePriority,
            selectedPlan.PickupBudget);

        DiagnosticTrace.Info(
            "haul.plan",
            () => $"Hard planner selected {selectedPlan.FirstPile.BlueprintId}: estTotal={selectedPlan.EstimatedTotal}, estResources={selectedPlan.EstimatedResourceTypes}, score={selectedPlan.Score:0.0}, storage={selectedPlan.PrimaryStorage.GetType().Name}[prio={selectedPlan.PrimaryStorage.Priority}], requested={selectedPlan.RequestedAmount}, destinationBudget={selectedPlan.DestinationBudget}, pickupBudget={selectedPlan.PickupBudget}, candidates={selectedPlan.CandidatePlan.Summarize()}",
            120);

        var clusterAugment = StockpileClusterAugmentor.Apply(
            goal,
            selectedPlan.FirstPile,
            selectedPlan.SourcePatchPiles,
            selectedPlan.PrimaryStorage,
            selectedPlan.CandidatePlan,
            selectedPlan.CandidatePlan.OrderedStorages,
            selectedPlan.DestinationBudget,
            StockpileHaulPolicy.SourceClusterExtent,
            StockpileHaulPolicy.PatchSweepExtent,
            StockpileHaulPolicy.PatchSweepLinkExtent,
            StockpileHaulPolicy.MixedGroundHarvestExtent,
            StockpileHaulPolicy.PatchSweepCountThreshold,
            StockpileHaulPolicy.PatchSweepAmountThreshold,
            StockpileHaulPolicy.MinimumDetourBudget,
            StockpileHaulPolicy.MaximumDetourBudget,
            StockpileHaulPolicy.DetourBudgetMultiplier,
            GetOptimisticPickupBudget,
            StockpileHaulingGoalState.ClearTargetsQueue,
            StockpileHaulingGoalState.QueueTarget);
        if (!clusterAugment.DestinationOutcome.Success)
        {
            StockpileHaulingGoalState.ResetGoalState(goal);
            StockpileTaskBoard.MarkFailed(selectedPlan.FirstPile);
            DiagnosticTrace.Info(
                "haul.plan",
                () => $"Hard planner failed to finalize destinations for {selectedPlan.FirstPile.BlueprintId}: {clusterAugment.DestinationOutcome.Summary}",
                120);
            return false;
        }

        if (clusterAugment.DestinationOutcome.LeasedAmount > 0)
        {
            DiagnosticTrace.Info(
                "haul.plan",
                () => $"Leased destination capacity for {selectedPlan.FirstPile.BlueprintId}: leased={clusterAugment.DestinationOutcome.LeasedAmount}, storages={clusterAugment.DestinationOutcome.StorageCount}, summary={clusterAugment.DestinationOutcome.Summary}",
                80);
        }

        return StockpileHaulingGoalState.FinalizeAugmentedPlan(goal, selectedPlan.SourcePriority, clusterAugment);
    }

    private static bool TryBuildPlayerForcedPlan(
        StockpileHaulingGoal goal,
        ResourcePileInstance anchorPile,
        IStorage? preferredStorage,
        PlayerForcedHaulIntentStore.PendingIntent? playerForcedIntent)
    {
        if (goal.AgentOwner is not CreatureBase creature || goal.AgentOwner is not IStorageAgent { Storage: not null } storageAgent)
        {
            return false;
        }

        if (anchorPile == null || anchorPile.HasDisposed || HaulFailureBackoffStore.IsCoolingDown(anchorPile))
        {
            return false;
        }

        var storedResource = anchorPile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return false;
        }

        var sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(anchorPile);
        var requestedAmount = GetOptimisticPickupBudget(goal, storedResource.Blueprint);
        var candidatePlan = StorageCandidatePlanner.BuildPlan(
            goal,
            creature,
            storedResource,
            ZonePriority.None,
            sourcePriority,
            enablePriorityFallback: false,
            requestedAmount,
            preferredStorage: preferredStorage);

        var primaryStorage = candidatePlan.Primary?.Storage;
        if (primaryStorage == null)
        {
            return false;
        }

        var destinationBudget = candidatePlan.GetEstimatedCapacityBudget(requestedAmount);
        if (destinationBudget <= 0)
        {
            return false;
        }

        var pickupBudget = Math.Max(1, Mathf.Min(destinationBudget, requestedAmount));
        var sourcePatchPiles = BuildPlayerForcedSourcePatch(anchorPile);
        var prioritySeedPiles = BuildPlayerForcedPrioritySeedPiles(anchorPile, playerForcedIntent);

        StockpileHaulingGoalState.ResetGoalState(goal);

        anchorPile.ReserveAll();
        if (!RuntimeServices.Reservations.TryReserveObject(anchorPile, goal.AgentOwner))
        {
            RuntimeServices.Reservations.ReleaseAll(anchorPile);
            return false;
        }

        StockpileHaulingGoalState.SeedInitialTargets(
            goal,
            anchorPile,
            primaryStorage,
            sourcePriority,
            pickupBudget);

        var addedPrioritySeeds = 0;
        foreach (var priorityPile in prioritySeedPiles.Skip(1))
        {
            if (!TryQueuePlayerForcedPrioritySeed(goal, creature, priorityPile))
            {
                continue;
            }

            StockpileHaulingGoalState.QueueTarget(goal, TargetIndex.A, new TargetObject(priorityPile));
            addedPrioritySeeds++;
        }

        if (addedPrioritySeeds > 0)
        {
            DiagnosticTrace.Info(
                "haul.takeover",
                () => $"Queued additional player-forced priority pickups for {goal.AgentOwner}: count={addedPrioritySeeds}, anchor={anchorPile.BlueprintId}",
                120);
        }

        var clusterAugment = StockpileClusterAugmentor.Apply(
            goal,
            anchorPile,
            sourcePatchPiles,
            primaryStorage,
            candidatePlan,
            candidatePlan.OrderedStorages,
            destinationBudget,
            StockpileHaulPolicy.PlayerForcedSourceClusterExtent,
            StockpileHaulPolicy.PlayerForcedSourceClusterExtent,
            StockpileHaulPolicy.PlayerForcedSourceClusterExtent,
            StockpileHaulPolicy.MixedGroundHarvestExtent,
            StockpileHaulPolicy.PatchSweepCountThreshold,
            StockpileHaulPolicy.PatchSweepAmountThreshold,
            StockpileHaulPolicy.MinimumDetourBudget,
            StockpileHaulPolicy.MaximumDetourBudget,
            StockpileHaulPolicy.DetourBudgetMultiplier,
            GetOptimisticPickupBudget,
            StockpileHaulingGoalState.ClearTargetsQueue,
            StockpileHaulingGoalState.QueueTarget);
        if (!clusterAugment.DestinationOutcome.Success)
        {
            StockpileHaulingGoalState.ResetGoalState(goal);
            return false;
        }

        return StockpileHaulingGoalState.FinalizeAugmentedPlan(goal, sourcePriority, clusterAugment);
    }

    private static PlannedSeedSelection? SelectSeedPlan(StockpileHaulingGoal goal, CreatureBase creature)
    {
        return StockpileTaskBoard.TryAssign(goal, creature, out var claimed)
            ? claimed
            : null;
    }

    private static PlannerMode DeterminePlannerMode(StockpileHaulingGoal goal)
    {
        if (goal == null)
        {
            return PlannerMode.Vanilla("null-goal");
        }

        if (CoordinatedStockpileTaskStore.TryGet(goal, out _))
        {
            return PlannerMode.Smart("existing-task");
        }

        if (goal.AgentOwner is CreatureBase creature &&
            CoordinatedStockpileIntentStore.TryConsume(creature))
        {
            return PlannerMode.Smart("idle-trigger-intent");
        }

        return PlannerMode.Vanilla("no-smart-intent");
    }

    private static string DescribeRecentGoal(CreatureBase? creature)
    {
        if (creature == null || !RecentGoalOriginStore.TryGetRecent(creature, out var recent))
        {
            return "<none>";
        }

        var age = RuntimeServices.Clock.RealtimeSinceStartup - recent.EndedAt;
        return $"{recent.GoalType}/{recent.Condition} action={recent.ActionId} age={age:0.00}s carry={recent.CarryCount} [{recent.CarrySummary}]";
    }

    private readonly struct ObservedHaulPlanState
    {
        public ObservedHaulPlanState(
            IReadOnlyList<ResourcePileInstance> haulQueue,
            IReadOnlyList<IStorage> storageQueue,
            ResourcePileInstance? firstPile,
            ResourceInstance? resourceSummary,
            IStorage? firstStorage,
            ZonePriority effectiveSourcePriority,
            float agentToSource)
        {
            HaulQueue = haulQueue;
            StorageQueue = storageQueue;
            FirstPile = firstPile;
            ResourceSummary = resourceSummary;
            FirstStorage = firstStorage;
            EffectiveSourcePriority = effectiveSourcePriority;
            AgentToSource = agentToSource;
        }

        public IReadOnlyList<ResourcePileInstance> HaulQueue { get; }

        public IReadOnlyList<IStorage> StorageQueue { get; }

        public ResourcePileInstance? FirstPile { get; }

        public ResourceInstance? ResourceSummary { get; }

        public IStorage? FirstStorage { get; }

        public ZonePriority EffectiveSourcePriority { get; }

        public float AgentToSource { get; }
    }

    private readonly struct PlannerMode
    {
        public PlannerMode(bool useCoordinatedPlanner, string reason)
        {
            UseCoordinatedPlanner = useCoordinatedPlanner;
            Reason = reason;
        }

        public bool UseCoordinatedPlanner { get; }
        public string Reason { get; }

        public static PlannerMode Smart(string reason) => new(true, reason);
        public static PlannerMode Vanilla(string reason) => new(false, reason);
    }

    internal static List<StockpileTaskSeed> BuildTaskSnapshotForBoard()
    {
        var candidates = new List<StockpileTaskSeed>();
        var claimedSourcePatches = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        foreach (var pile in StockpilePileTopology.GetCentralHaulSourcePiles()
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
        return StockpilePileTopology.GetCentralHaulSourcePiles();
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
        return HaulingScore.CalculateBoardAssignmentScore(
            selection.Score,
            distanceToSource,
            selection.SourcePriority,
            selection.PrimaryStorage.Priority);
    }

    private static StockpileTaskSeed? TryCreateTaskSeed(
        ResourcePileInstance firstPile)
    {
        return StockpileTaskSeedFactory.TryCreate(
            firstPile,
            StockpilePileTopology.GetCentralHaulSourcePiles(),
            StockpileHaulPolicy.SourceClusterExtent,
            StockpileHaulPolicy.MixedGroundHarvestExtent,
            StockpileHaulPolicy.PatchSweepAmountThreshold,
            StockpileHaulPolicy.PatchSweepCountThreshold,
            StockpileHaulPolicy.PatchSweepExtent,
            StockpileHaulPolicy.PatchSweepLinkExtent,
            StockpileHaulPolicy.MinimumSourceSliceWeightBudget,
            StockpileTaskBoard.GetNominalWorkerFreeSpace());
    }

    private static List<ResourcePileInstance> BuildPlayerForcedSourcePatch(ResourcePileInstance anchorPile)
    {
        return StockpilePileTopology.GetCentralHaulSourcePiles()
            .Where(pile =>
                pile != null &&
                !pile.HasDisposed &&
                pile.Blueprint == anchorPile.Blueprint &&
                Vector3.Distance(anchorPile.GetPosition(), pile.GetPosition()) <= StockpileHaulPolicy.PlayerForcedSourceClusterExtent)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .Prepend(anchorPile)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList();
    }

    private static List<ResourcePileInstance> BuildPlayerForcedPrioritySeedPiles(
        ResourcePileInstance anchorPile,
        PlayerForcedHaulIntentStore.PendingIntent? playerForcedIntent)
    {
        var priorityPiles = playerForcedIntent.HasValue ? playerForcedIntent.Value.PriorityPiles : Array.Empty<ResourcePileInstance>();
        return PlayerForcedPriorityPlanner.SelectLocalPrioritySeeds(
                anchorPile,
                priorityPiles,
                StockpileHaulPolicy.PlayerForcedSourceClusterExtent,
                pile => pile != null && !pile.HasDisposed,
                pile => Vector3.Distance(anchorPile.GetPosition(), pile.GetPosition()),
                ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList();
    }

    private static bool TryQueuePlayerForcedPrioritySeed(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance pile)
    {
        if (pile == null ||
            pile.HasDisposed ||
            HaulFailureBackoffStore.IsCoolingDown(pile) ||
            !ClusterOwnershipStore.CanUsePile(creature, pile) ||
            !HaulSourcePolicy.CanReachPile(goal, pile) ||
            !HaulSourcePolicy.ValidatePile(goal, pile))
        {
            return false;
        }

        pile.ReserveAll();
        if (RuntimeServices.Reservations.TryReserveObject(pile, goal.AgentOwner))
        {
            return true;
        }

        RuntimeServices.Reservations.ReleaseAll(pile);
        return false;
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

        return Math.Max(1, StockpileHaulingGoalState.GetMaxCarryAmount(goal));
    }

    internal static string DescribeTaskSeeds(IEnumerable<StockpileTaskSeed> seeds, int maxSeeds = 6)
    {
        return HaulingDecisionTraceDiagnostics.DescribeTaskSeeds(seeds, maxSeeds);
    }
}

