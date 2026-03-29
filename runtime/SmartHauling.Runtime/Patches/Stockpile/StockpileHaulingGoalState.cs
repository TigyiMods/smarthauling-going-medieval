using HarmonyLib;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime.Patches;

internal static class StockpileHaulingGoalState
{
    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> TotalTargetedCountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("TotalTargetedCount");

    private static readonly AccessTools.FieldRef<HaulingBaseGoal, int> MaxCarryAmountRef =
        AccessTools.FieldRefAccess<HaulingBaseGoal, int>("MaxCaryAmount");

    internal static int GetTotalTargetedCount(StockpileHaulingGoal goal) => TotalTargetedCountRef(goal);

    internal static int GetMaxCarryAmount(StockpileHaulingGoal goal) => MaxCarryAmountRef(goal);

    internal static void SeedInitialTargets(
        StockpileHaulingGoal goal,
        ResourcePileInstance firstPile,
        NSMedieval.IStorage primaryStorage,
        ZonePriority sourcePriority,
        int pickupBudget)
    {
        QueueTarget(goal, TargetIndex.A, new TargetObject(firstPile));
        QueueTarget(goal, TargetIndex.B, new TargetObject(primaryStorage));
        GoalSourcePriorityStore.Set(goal, sourcePriority);
        MaxCarryAmountRef(goal) = pickupBudget;
        TotalTargetedCountRef(goal) = 0;
    }

    internal static bool FinalizeAugmentedPlan(
        StockpileHaulingGoal goal,
        ZonePriority sourcePriority,
        StockpileClusterAugmentResult clusterAugment)
    {
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
                sourcePriority,
                plannedPickups,
                destinationPlan);
            if (CoordinatedStockpileTaskStore.TryGet(goal, out var task))
            {
                RewritePickupQueue(goal, task.PlannedPickups);
            }
        }

        return TotalTargetedCountRef(goal) > 0 &&
               goal.GetTargetQueue(TargetIndex.A).Count > 0 &&
               StockpileDestinationPlanStore.TryGet(goal, out _);
    }

    internal static VanillaHaulPlanSnapshot CaptureVanillaPlan(StockpileHaulingGoal goal, bool result)
    {
        var sourceTargets = goal.GetTargetQueue(TargetIndex.A).ToList();
        var storageTargets = goal.GetTargetQueue(TargetIndex.B).ToList();
        var firstBlueprintId = sourceTargets.FirstOrDefault().GetObjectAs<ResourcePileInstance>()?.BlueprintId ?? "<none>";
        return new VanillaHaulPlanSnapshot(
            sourceTargets,
            storageTargets,
            TotalTargetedCountRef(goal),
            MaxCarryAmountRef(goal),
            result,
            firstBlueprintId);
    }

    internal static void RestoreVanillaPlan(StockpileHaulingGoal goal, VanillaHaulPlanSnapshot snapshot)
    {
        ResetGoalState(goal);
        foreach (var target in snapshot.SourceTargets)
        {
            QueueTarget(goal, TargetIndex.A, target);
            if (target.GetObjectAs<ResourcePileInstance>() is { } pile)
            {
                pile.ReserveAll();
                RuntimeServices.Reservations.TryReserveObject(pile, goal.AgentOwner);
            }
        }

        foreach (var target in snapshot.StorageTargets)
        {
            QueueTarget(goal, TargetIndex.B, target);
        }

        TotalTargetedCountRef(goal) = snapshot.TotalTargetedCount;
        MaxCarryAmountRef(goal) = snapshot.MaxCarryAmount;
    }

    internal static void ResetGoalState(StockpileHaulingGoal goal)
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

    internal static void ClearTargetsQueue(Goal goal, TargetIndex index)
    {
        GoalTargetQueueAccess.ClearTargetsQueue(goal, index);
    }

    internal static void QueueTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        GoalTargetQueueAccess.QueueTarget(goal, index, target);
    }

    private static void RewritePickupQueue(Goal goal, IReadOnlyList<ResourcePileInstance> plannedPickups)
    {
        if (goal == null)
        {
            return;
        }

        ClearTargetsQueue(goal, TargetIndex.A);
        foreach (var pile in plannedPickups.Where(pile => pile != null && !pile.HasDisposed))
        {
            QueueTarget(goal, TargetIndex.A, new TargetObject(pile));
        }
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
}

internal readonly struct VanillaHaulPlanSnapshot
{
    public VanillaHaulPlanSnapshot(
        IReadOnlyList<TargetObject> sourceTargets,
        IReadOnlyList<TargetObject> storageTargets,
        int totalTargetedCount,
        int maxCarryAmount,
        bool result,
        string firstBlueprintId)
    {
        SourceTargets = sourceTargets;
        StorageTargets = storageTargets;
        TotalTargetedCount = totalTargetedCount;
        MaxCarryAmount = maxCarryAmount;
        Result = result;
        FirstBlueprintId = firstBlueprintId;
    }

    public IReadOnlyList<TargetObject> SourceTargets { get; }

    public IReadOnlyList<TargetObject> StorageTargets { get; }

    public int TotalTargetedCount { get; }

    public int MaxCarryAmount { get; }

    public bool Result { get; }

    public string FirstBlueprintId { get; }
}
