using System.Runtime.CompilerServices;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Model;

namespace SmartHauling.Runtime;

internal sealed class MixedCollectPlan
{
    public MixedCollectPlan(Dictionary<string, int> requestedByResourceId, IStorage? anchorStorage = null)
    {
        RequestedByResourceId = requestedByResourceId;
        AnchorStorage = anchorStorage;
        StartedMixed = requestedByResourceId.Count > 1 || anchorStorage != null;
    }

    public Dictionary<string, int> RequestedByResourceId { get; }

    public IStorage? AnchorStorage { get; }

    public bool StartedMixed { get; private set; }

    public int TotalRemaining => RequestedByResourceId.Values.Sum();

    public void MarkStartedMixed()
    {
        StartedMixed = true;
    }
}

internal static class MixedCollectPlanStore
{
    private static readonly ConditionalWeakTable<Goal, MixedCollectPlan> Plans = new();

    public static void Set(Goal goal, Dictionary<string, int> requestedByResourceId, IStorage? anchorStorage = null)
    {
        if (goal == null)
        {
            return;
        }

        Clear(goal);
        Plans.Add(goal, new MixedCollectPlan(requestedByResourceId, anchorStorage));
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        Plans.Remove(goal);
    }

    public static bool HasMixedPlan(Goal goal)
    {
        return TryGet(goal, out var plan) && plan.StartedMixed;
    }

    public static int GetRequestedAmount(Goal goal, Resource blueprint, int fallbackAmount, out bool usedPlan)
    {
        usedPlan = false;

        if (goal == null || blueprint == null || !TryGet(goal, out var plan))
        {
            return fallbackAmount;
        }

        usedPlan = true;
        return plan.RequestedByResourceId.TryGetValue(blueprint.GetID(), out var amount) ? amount : 0;
    }

    public static void Consume(Goal goal, Resource blueprint, int count)
    {
        if (goal == null || blueprint == null || count <= 0 || !TryGet(goal, out var plan))
        {
            return;
        }

        var resourceId = blueprint.GetID();
        if (!plan.RequestedByResourceId.TryGetValue(resourceId, out var remaining))
        {
            return;
        }

        remaining -= count;
        if (remaining <= 0)
        {
            plan.RequestedByResourceId.Remove(resourceId);
        }
        else
        {
            plan.RequestedByResourceId[resourceId] = remaining;
        }
    }

    public static void AppendRequestedAmount(Goal goal, Resource blueprint, int count)
    {
        if (goal == null || blueprint == null || count <= 0)
        {
            return;
        }

        if (!TryGet(goal, out var plan))
        {
            Set(goal, new Dictionary<string, int>
            {
                [blueprint.GetID()] = count
            });
            return;
        }

        var resourceId = blueprint.GetID();
        plan.RequestedByResourceId[resourceId] = plan.RequestedByResourceId.TryGetValue(resourceId, out var current)
            ? current + count
            : count;
    }

    public static void MarkStartedMixed(Goal goal)
    {
        if (goal == null || !TryGet(goal, out var plan))
        {
            return;
        }

        plan.MarkStartedMixed();
    }

    public static bool TryGet(Goal goal, out MixedCollectPlan plan)
    {
        if (goal != null && Plans.TryGetValue(goal, out plan!))
        {
            return true;
        }

        plan = null!;
        return false;
    }

    public static bool TryGetAnchorStorage(Goal goal, out IStorage storage)
    {
        if (TryGet(goal, out var plan) && plan.AnchorStorage != null && !plan.AnchorStorage.HasDisposed)
        {
            storage = plan.AnchorStorage;
            return true;
        }

        storage = null!;
        return false;
    }
}
