using System.Runtime.CompilerServices;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.State;

namespace SmartHauling.Runtime;

/// <summary>
/// Immutable per-resource destination plan for a single coordinated stockpile task.
/// </summary>
internal sealed class CoordinatedStockpileDropPlan
{
    public CoordinatedStockpileDropPlan(
        string resourceId,
        IReadOnlyList<IStorage> orderedStorages,
        int requestedAmount,
        IReadOnlyList<StockpileStorageAllocation>? plannedAllocations = null)
    {
        ResourceId = resourceId;
        OrderedStorages = orderedStorages;
        RequestedAmount = requestedAmount;
        PlannedAllocations = plannedAllocations ?? Array.Empty<StockpileStorageAllocation>();
    }

    public string ResourceId { get; }

    public IReadOnlyList<IStorage> OrderedStorages { get; }

    public int RequestedAmount { get; }

    public IReadOnlyList<StockpileStorageAllocation> PlannedAllocations { get; }

    /// <summary>
    /// Returns currently usable storages after filtering disposed or duplicated entries.
    /// </summary>
    public IReadOnlyList<IStorage> GetActiveStorages()
    {
        var activeAllocations = GetActiveAllocations();
        if (activeAllocations.Count > 0)
        {
            return activeAllocations.Select(allocation => allocation.Storage).ToList();
        }

        return OrderedStorages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
    }

    public IReadOnlyList<StockpileStorageAllocation> GetActiveAllocations()
    {
        return StorageAllocationPlanBuilder.MergeAllocations(PlannedAllocations);
    }
}

/// <summary>
/// Immutable stockpile hauling task materialized by the orchestrator for a single goal.
/// </summary>
/// <remarks>
/// This object is the contract between centralized planning and the executor. It should be treated
/// as a fixed task script for the lifetime of the goal, aside from runtime execution state tracked elsewhere.
/// </remarks>
internal sealed class CoordinatedStockpileTask
{
    private readonly Dictionary<string, CoordinatedStockpileDropPlan> dropPlansByResourceId;

    public CoordinatedStockpileTask(
        int pickupBudget,
        ZonePriority sourcePriority,
        IReadOnlyList<ResourcePileInstance> plannedPickups,
        string primaryResourceId,
        IReadOnlyList<CoordinatedStockpileDropPlan> dropPlans)
    {
        PickupBudget = pickupBudget;
        SourcePriority = sourcePriority;
        PrimaryResourceId = primaryResourceId;
        dropPlansByResourceId = dropPlans
            .Where(plan => plan != null && !string.IsNullOrWhiteSpace(plan.ResourceId))
            .ToDictionary(plan => plan.ResourceId);
        PlannedPickups = CoordinatedPickupRouteOrdering.OrderPlannedPickups(
            plannedPickups,
            BuildDropAnchorMap(dropPlansByResourceId));
        DropOrder = CoordinatedPickupRouteOrdering.BuildDropOrder(
            primaryResourceId,
            PlannedPickups,
            pile => pile.BlueprintId,
            dropPlansByResourceId.Values
                .OrderByDescending(plan => plan.RequestedAmount)
                .ThenBy(plan => plan.ResourceId)
                .Select(plan => plan.ResourceId));
    }

    public int PickupBudget { get; }

    public ZonePriority SourcePriority { get; }

    public IReadOnlyList<ResourcePileInstance> PlannedPickups { get; }

    public string PrimaryResourceId { get; }

    public IReadOnlyList<string> DropOrder { get; }

    /// <summary>
    /// Resolves the drop plan for a specific resource, falling back to the primary resource when needed.
    /// </summary>
    public bool TryGetDropPlan(string? resourceId, out CoordinatedStockpileDropPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(resourceId) && dropPlansByResourceId.TryGetValue(resourceId, out plan!))
        {
            return plan.GetActiveStorages().Count > 0;
        }

        if (!string.IsNullOrWhiteSpace(PrimaryResourceId) && dropPlansByResourceId.TryGetValue(PrimaryResourceId, out plan!))
        {
            return plan.GetActiveStorages().Count > 0;
        }

        plan = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<UnityEngine.Vector3>> BuildDropAnchorMap(
        IReadOnlyDictionary<string, CoordinatedStockpileDropPlan> plans)
    {
        var anchors = new Dictionary<string, IReadOnlyList<UnityEngine.Vector3>>();
        foreach (var entry in plans)
        {
            var positions = entry.Value.GetActiveAllocations()
                .Select(allocation => allocation.Storage)
                .Concat(entry.Value.GetActiveStorages())
                .Where(storage => storage != null && !storage.HasDisposed)
                .Select(storage => StorageCandidatePlanner.TryGetPosition(storage))
                .Where(position => position.HasValue)
                .Select(position => position!.Value)
                .Distinct()
                .ToList();

            if (positions.Count == 0)
            {
                var fallbackStorage = entry.Value.GetActiveStorages().FirstOrDefault();
                var fallbackPosition = StorageCandidatePlanner.TryGetPosition(fallbackStorage);
                if (fallbackPosition.HasValue)
                {
                    positions.Add(fallbackPosition.Value);
                }
            }

            if (positions.Count > 0)
            {
                anchors[entry.Key] = positions;
            }
        }

        return anchors;
    }
}

/// <summary>
/// Goal-scoped storage for the immutable coordinated stockpile task assigned to a hauling goal.
/// </summary>
internal static class CoordinatedStockpileTaskStore
{
    private static readonly ConditionalWeakTable<Goal, CoordinatedStockpileTask> Tasks = new();

    public static void Set(
        Goal goal,
        int pickupBudget,
        ZonePriority sourcePriority,
        IReadOnlyList<ResourcePileInstance> plannedPickups,
        StockpileDestinationPlan destinationPlan)
    {
        if (goal == null || destinationPlan == null)
        {
            return;
        }

        var dropPlans = destinationPlan.ResourcePlans
            .Select(plan => new CoordinatedStockpileDropPlan(
                plan.ResourceId,
                plan.GetActiveStorages(),
                plan.RequestedAmount,
                plan.GetActiveAllocations()))
            .Where(plan => plan.OrderedStorages.Count > 0)
            .ToList();

        if (dropPlans.Count == 0)
        {
            Clear(goal);
            return;
        }

        Clear(goal);
        Tasks.Add(goal, new CoordinatedStockpileTask(
            pickupBudget,
            sourcePriority,
            plannedPickups.Where(pile => pile != null && !pile.HasDisposed).ToList(),
            destinationPlan.PrimaryResourceId,
            dropPlans));
    }

    public static bool TryGet(Goal goal, out CoordinatedStockpileTask task)
    {
        if (goal != null && Tasks.TryGetValue(goal, out task!))
        {
            return true;
        }

        task = null!;
        return false;
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        Tasks.Remove(goal);
    }
}
