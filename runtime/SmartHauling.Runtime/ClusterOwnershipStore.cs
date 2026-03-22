using System.Collections.Generic;
using System.Linq;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class ClusterOwnershipStore
{
    private const float ClaimLifetimeSeconds = 20f;
    private const float MinimumPatchRadius = 8f;
    private const float PatchPadding = 4f;

    private static readonly Dictionary<ResourcePileInstance, ClusterClaim> ClaimsByPile =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private static readonly List<PatchClaim> PatchClaims = new();

    public static bool CanUsePile(CreatureBase owner, ResourcePileInstance pile)
    {
        if (pile == null)
        {
            return true;
        }

        CleanupPile(pile);
        CleanupPatchClaims();

        if (!ClaimsByPile.TryGetValue(pile, out var claim))
        {
            return CanUsePatch(owner, pile);
        }

        if (claim.Owner == null || claim.Owner.HasDisposed || ReferenceEquals(claim.Owner, owner))
        {
            return CanUsePatch(owner, pile);
        }

        if (claim.ExpiresAt <= Time.realtimeSinceStartup)
        {
            ClaimsByPile.Remove(pile);
            return CanUsePatch(owner, pile);
        }

        return false;
    }

    public static int ClaimCluster(Goal goal, CreatureBase owner, IEnumerable<ResourcePileInstance> piles)
    {
        var claimed = 0;
        var expiresAt = Time.realtimeSinceStartup + ClaimLifetimeSeconds;
        var distinctPiles = piles
            .Where(pile => pile != null)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList();

        foreach (var pile in distinctPiles)
        {
            CleanupPile(pile);

            if (ClaimsByPile.TryGetValue(pile, out var existing) &&
                existing.Owner != null &&
                !existing.Owner.HasDisposed &&
                !ReferenceEquals(existing.Owner, owner) &&
                existing.ExpiresAt > Time.realtimeSinceStartup)
            {
                continue;
            }

            ClaimsByPile[pile] = new ClusterClaim(owner, goal, expiresAt);
            claimed++;
        }

        var groupedByBlueprint = distinctPiles
            .Where(pile => !pile.HasDisposed)
            .GroupBy(pile => pile.Blueprint);

        foreach (var group in groupedByBlueprint)
        {
            var seedPile = group.FirstOrDefault();
            if (seedPile == null)
            {
                continue;
            }

            var radius = Mathf.Max(
                MinimumPatchRadius,
                group.Max(pile => Vector3.Distance(seedPile.GetPosition(), pile.GetPosition())) + PatchPadding);

            PatchClaims.RemoveAll(existing => existing.Goal != null && ReferenceEquals(existing.Goal, goal) && existing.Blueprint == group.Key);
            PatchClaims.Add(new PatchClaim(owner, goal, group.Key, seedPile.GetPosition(), radius, expiresAt));
        }

        return claimed;
    }

    public static void RefreshOwner(CreatureBase owner)
    {
        var now = Time.realtimeSinceStartup;
        var keys = ClaimsByPile
            .Where(entry => entry.Value.Owner != null && ReferenceEquals(entry.Value.Owner, owner))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var pile in keys)
        {
            if (ClaimsByPile.TryGetValue(pile, out var claim))
            {
                claim.ExpiresAt = now + ClaimLifetimeSeconds;
            }
        }

        foreach (var patchClaim in PatchClaims.Where(entry => entry.Owner != null && ReferenceEquals(entry.Owner, owner)))
        {
            patchClaim.ExpiresAt = now + ClaimLifetimeSeconds;
        }
    }

    public static void RefreshGoal(Goal goal)
    {
        var now = Time.realtimeSinceStartup;
        var keys = ClaimsByPile
            .Where(entry => entry.Value.Goal != null && ReferenceEquals(entry.Value.Goal, goal))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var pile in keys)
        {
            if (ClaimsByPile.TryGetValue(pile, out var claim))
            {
                claim.ExpiresAt = now + ClaimLifetimeSeconds;
            }
        }
    }

    public static void ReleaseGoal(Goal goal)
    {
        var keys = ClaimsByPile
            .Where(entry => entry.Value.Goal != null && ReferenceEquals(entry.Value.Goal, goal))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var pile in keys)
        {
            ClaimsByPile.Remove(pile);
        }

        PatchClaims.RemoveAll(entry => entry.Goal != null && ReferenceEquals(entry.Goal, goal));
    }

    private static bool CanUsePatch(CreatureBase owner, ResourcePileInstance pile)
    {
        foreach (var claim in PatchClaims)
        {
            if (claim.Blueprint != pile.Blueprint)
            {
                continue;
            }

            if (claim.Owner == null || claim.Owner.HasDisposed || ReferenceEquals(claim.Owner, owner))
            {
                continue;
            }

            if (Vector3.Distance(claim.Center, pile.GetPosition()) <= claim.Radius)
            {
                return false;
            }
        }

        return true;
    }

    private static void CleanupPile(ResourcePileInstance pile)
    {
        if (pile == null)
        {
            return;
        }

        if (!ClaimsByPile.TryGetValue(pile, out var claim))
        {
            return;
        }

        if (pile.HasDisposed ||
            claim.Owner == null ||
            claim.Owner.HasDisposed ||
            claim.ExpiresAt <= Time.realtimeSinceStartup)
        {
            ClaimsByPile.Remove(pile);
        }
    }

    private static void CleanupPatchClaims()
    {
        var now = Time.realtimeSinceStartup;
        PatchClaims.RemoveAll(claim =>
            claim.Owner == null ||
            claim.Owner.HasDisposed ||
            claim.Goal == null ||
            claim.ExpiresAt <= now);
    }

    private sealed class ClusterClaim
    {
        public ClusterClaim(CreatureBase owner, Goal goal, float expiresAt)
        {
            Owner = owner;
            Goal = goal;
            ExpiresAt = expiresAt;
        }

        public CreatureBase Owner { get; }

        public Goal Goal { get; }

        public float ExpiresAt { get; set; }
    }

    private sealed class PatchClaim
    {
        public PatchClaim(CreatureBase owner, Goal goal, Resource blueprint, Vector3 center, float radius, float expiresAt)
        {
            Owner = owner;
            Goal = goal;
            Blueprint = blueprint;
            Center = center;
            Radius = radius;
            ExpiresAt = expiresAt;
        }

        public CreatureBase Owner { get; }

        public Goal Goal { get; }

        public Resource Blueprint { get; }

        public Vector3 Center { get; }

        public float Radius { get; }

        public float ExpiresAt { get; set; }
    }
}
