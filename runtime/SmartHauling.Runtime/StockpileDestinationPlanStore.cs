using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NSMedieval;
using NSMedieval.Goap;

namespace SmartHauling.Runtime;

internal sealed class StockpileDestinationResourcePlan
{
    public StockpileDestinationResourcePlan(string resourceId, IReadOnlyList<IStorage> orderedStorages, int requestedAmount)
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

internal sealed class StockpileDestinationPlan
{
    private readonly Dictionary<string, StockpileDestinationResourcePlan> plansByResourceId;

    public StockpileDestinationPlan(string primaryResourceId, IReadOnlyDictionary<string, StockpileDestinationResourcePlan> plans)
    {
        PrimaryResourceId = primaryResourceId;
        plansByResourceId = new Dictionary<string, StockpileDestinationResourcePlan>(plans);
    }

    public string PrimaryResourceId { get; }

    public IReadOnlyCollection<StockpileDestinationResourcePlan> ResourcePlans => plansByResourceId.Values;

    public bool TryGetResourcePlan(string? resourceId, out StockpileDestinationResourcePlan plan)
    {
        if (!string.IsNullOrWhiteSpace(resourceId) && plansByResourceId.TryGetValue(resourceId, out plan!))
        {
            return plan.GetActiveStorages().Count > 0;
        }

        if (!string.IsNullOrWhiteSpace(PrimaryResourceId) && plansByResourceId.TryGetValue(PrimaryResourceId, out plan!))
        {
            return plan.GetActiveStorages().Count > 0;
        }

        plan = null!;
        return false;
    }

    public IReadOnlyList<IStorage> GetActiveStorages(string? resourceId = null)
    {
        return TryGetResourcePlan(resourceId, out var plan)
            ? plan.GetActiveStorages()
            : new List<IStorage>();
    }

    public int GetRequestedAmount(string? resourceId = null)
    {
        return TryGetResourcePlan(resourceId, out var plan)
            ? plan.RequestedAmount
            : 0;
    }
}

internal static class StockpileDestinationPlanStore
{
    private static readonly ConditionalWeakTable<Goal, StockpileDestinationPlan> Plans = new();

    public static void Set(Goal goal, IReadOnlyList<IStorage> orderedStorages, int requestedAmount, string primaryResourceId)
    {
        if (goal == null)
        {
            return;
        }

        if (orderedStorages == null || orderedStorages.Count == 0 || string.IsNullOrWhiteSpace(primaryResourceId))
        {
            Clear(goal);
            return;
        }

        var plan = new StockpileDestinationResourcePlan(
            primaryResourceId,
            orderedStorages
                .Where(storage => storage != null)
                .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
                .ToList(),
            requestedAmount);

        Set(goal, primaryResourceId, new[] { plan });
    }

    public static void Set(Goal goal, string primaryResourceId, IEnumerable<StockpileDestinationResourcePlan> resourcePlans)
    {
        if (goal == null)
        {
            return;
        }

        var plans = resourcePlans?
            .Where(plan =>
                plan != null &&
                !string.IsNullOrWhiteSpace(plan.ResourceId) &&
                plan.OrderedStorages != null &&
                plan.OrderedStorages.Count > 0)
            .GroupBy(plan => plan.ResourceId)
            .ToDictionary(
                group => group.Key,
                group => new StockpileDestinationResourcePlan(
                    group.Key,
                    group.SelectMany(plan => plan.OrderedStorages)
                        .Where(storage => storage != null)
                        .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
                        .ToList(),
                    group.Max(plan => plan.RequestedAmount)))
            ?? new Dictionary<string, StockpileDestinationResourcePlan>();

        if (plans.Count == 0 || string.IsNullOrWhiteSpace(primaryResourceId))
        {
            Clear(goal);
            return;
        }

        if (!plans.ContainsKey(primaryResourceId))
        {
            primaryResourceId = plans.Keys.First();
        }

        Clear(goal);
        Plans.Add(goal, new StockpileDestinationPlan(primaryResourceId, plans));
    }

    public static bool TryGet(Goal goal, out StockpileDestinationPlan plan)
    {
        if (goal == null)
        {
            plan = null!;
            return false;
        }

        if (Plans.TryGetValue(goal, out plan!) &&
            plan.ResourcePlans.Any(entry => entry.GetActiveStorages().Count > 0))
        {
            return true;
        }

        plan = null!;
        return false;
    }

    public static bool TryGetActiveStorages(Goal goal, string? resourceId, out IReadOnlyList<IStorage> storages)
    {
        if (TryGet(goal, out var plan))
        {
            storages = plan.GetActiveStorages(resourceId);
            if (storages.Count > 0)
            {
                return true;
            }
        }

        storages = null!;
        return false;
    }

    public static int GetRequestedAmount(Goal goal, string? resourceId)
    {
        return TryGet(goal, out var plan) ? plan.GetRequestedAmount(resourceId) : 0;
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        Plans.Remove(goal);
    }

    public static void MergeResourcePlan(
        Goal goal,
        string resourceId,
        IEnumerable<IStorage> orderedStorages,
        int requestedAmount)
    {
        if (goal == null || string.IsNullOrWhiteSpace(resourceId) || orderedStorages == null)
        {
            return;
        }

        var mergedPlans = new Dictionary<string, StockpileDestinationResourcePlan>();
        var primaryResourceId = resourceId;
        if (TryGet(goal, out var existingPlan))
        {
            primaryResourceId = existingPlan.PrimaryResourceId;
            foreach (var existingResourcePlan in existingPlan.ResourcePlans)
            {
                mergedPlans[existingResourcePlan.ResourceId] = new StockpileDestinationResourcePlan(
                    existingResourcePlan.ResourceId,
                    existingResourcePlan.GetActiveStorages(),
                    existingResourcePlan.RequestedAmount);
            }
        }

        var activeStorages = orderedStorages
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
        if (activeStorages.Count == 0)
        {
            return;
        }

        if (mergedPlans.TryGetValue(resourceId, out var currentPlan))
        {
            mergedPlans[resourceId] = new StockpileDestinationResourcePlan(
                resourceId,
                currentPlan.GetActiveStorages()
                    .Concat(activeStorages)
                    .Where(storage => storage != null && !storage.HasDisposed)
                    .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
                    .ToList(),
                System.Math.Max(currentPlan.RequestedAmount, requestedAmount));
        }
        else
        {
            mergedPlans[resourceId] = new StockpileDestinationResourcePlan(
                resourceId,
                activeStorages,
                requestedAmount);
        }

        Set(goal, primaryResourceId, mergedPlans.Values);
    }
}
