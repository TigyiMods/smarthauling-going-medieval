using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.State;

namespace SmartHauling.Runtime;

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

    public IReadOnlyList<IStorage> GetActiveStorages()
    {
        return OrderedStorages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
    }
}

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
