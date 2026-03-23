using NSMedieval.Goap;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime;

internal static class ProductionTargetOwnershipStore
{
    private const float ClaimLifetimeSeconds = 15f;

    private static readonly Dictionary<ProductionInstance, ProductionClaim> ClaimsByProduction = new();

    public static bool TryClaim(Goal goal, CreatureBase owner, ProductionInstance production)
    {
        if (goal == null || owner == null || owner.HasDisposed || production == null)
        {
            return false;
        }

        Cleanup(production);
        if (ClaimsByProduction.TryGetValue(production, out var existing))
        {
            if (existing.Owner != null &&
                !existing.Owner.HasDisposed &&
                existing.Goal != null &&
                existing.Goal.State != GoalState.Ended &&
                !ReferenceEquals(existing.Goal, goal) &&
                existing.ExpiresAt > RuntimeServices.Clock.RealtimeSinceStartup)
            {
                return false;
            }
        }

        ClaimsByProduction[production] = new ProductionClaim(owner, goal, RuntimeServices.Clock.RealtimeSinceStartup + ClaimLifetimeSeconds);
        return true;
    }

    public static void ReleaseGoal(Goal goal)
    {
        var toRemove = new List<ProductionInstance>();
        foreach (var entry in ClaimsByProduction)
        {
            if (ReferenceEquals(entry.Value.Goal, goal))
            {
                toRemove.Add(entry.Key);
            }
        }

        foreach (var production in toRemove)
        {
            ClaimsByProduction.Remove(production);
        }
    }

    private static void Cleanup(ProductionInstance production)
    {
        if (!ClaimsByProduction.TryGetValue(production, out var claim))
        {
            return;
        }

        if (claim.Owner == null ||
            claim.Owner.HasDisposed ||
            claim.Goal == null ||
            claim.Goal.State == GoalState.Ended ||
            claim.ExpiresAt <= RuntimeServices.Clock.RealtimeSinceStartup)
        {
            ClaimsByProduction.Remove(production);
        }
    }

    private sealed class ProductionClaim
    {
        public ProductionClaim(CreatureBase owner, Goal goal, float expiresAt)
        {
            Owner = owner;
            Goal = goal;
            ExpiresAt = expiresAt;
        }

        public CreatureBase Owner { get; }

        public Goal Goal { get; }

        public float ExpiresAt { get; }
    }
}
