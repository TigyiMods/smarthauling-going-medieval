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
    public CoordinatedStockpileDropPlan(string resourceId, IReadOnlyList<IStorage> orderedStorages, int requestedAmount)
    {
        ResourceId = resourceId;
        OrderedStorages = orderedStorages;
        RequestedAmount = requestedAmount;
    }

    public string ResourceId { get; }

    public IReadOnlyList<IStorage> OrderedStorages { get; }

    public int RequestedAmount { get; }

    /// <summary>
    /// Returns currently usable storages after filtering disposed or duplicated entries.
    /// </summary>
    public IReadOnlyList<IStorage> GetActiveStorages()
    {
        return OrderedStorages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
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
        PlannedPickups = plannedPickups;
        PrimaryResourceId = primaryResourceId;
        dropPlansByResourceId = dropPlans
            .Where(plan => plan != null && !string.IsNullOrWhiteSpace(plan.ResourceId))
            .ToDictionary(plan => plan.ResourceId);
        DropOrder = BuildDropOrder(primaryResourceId, dropPlansByResourceId);
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

    private static IReadOnlyList<string> BuildDropOrder(
        string primaryResourceId,
        IReadOnlyDictionary<string, CoordinatedStockpileDropPlan> plans)
    {
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryResourceId) && plans.ContainsKey(primaryResourceId))
        {
            ordered.Add(primaryResourceId);
        }

        ordered.AddRange(plans.Values
            .Where(plan => !string.Equals(plan.ResourceId, primaryResourceId))
            .OrderByDescending(plan => plan.RequestedAmount)
            .ThenBy(plan => plan.ResourceId)
            .Select(plan => plan.ResourceId));

        return ordered;
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
                plan.RequestedAmount))
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
