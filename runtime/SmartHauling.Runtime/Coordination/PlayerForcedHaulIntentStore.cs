using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class PlayerForcedHaulIntentStore
{
    private const float PendingIntentLifetimeSeconds = 3f;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<CreatureBase, PendingIntent> PendingByCreature =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);

    public static void MarkPending(CreatureBase creature, ResourcePileInstance anchorPile)
    {
        if (creature == null || anchorPile == null)
        {
            return;
        }

        var blueprintId = anchorPile.BlueprintId ?? anchorPile.GetStoredResource()?.BlueprintId ?? "<unknown>";
        lock (SyncRoot)
        {
            CleanupExpired();
            var existingPiles = PendingByCreature.TryGetValue(creature, out var existingIntent)
                ? existingIntent.PriorityPiles.Where(pile => pile != null && !pile.HasDisposed)
                : Enumerable.Empty<ResourcePileInstance>();
            var priorityPiles = PlayerForcedPriorityPlanner.MergePriorityOrder(
                anchorPile,
                existingPiles,
                ReferenceEqualityComparer<ResourcePileInstance>.Instance);
            PendingByCreature[creature] = new PendingIntent(
                anchorPile,
                blueprintId,
                anchorPile.GetPosition(),
                RuntimeServices.Clock.RealtimeSinceStartup,
                priorityPiles);
        }
    }

    public static bool TryPeek(CreatureBase creature, out PendingIntent intent)
    {
        if (creature == null)
        {
            intent = default;
            return false;
        }

        lock (SyncRoot)
        {
            CleanupExpired();
            return PendingByCreature.TryGetValue(creature, out intent);
        }
    }

    public static void Clear(CreatureBase creature)
    {
        if (creature == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            PendingByCreature.Remove(creature);
        }
    }

    private static void CleanupExpired()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var expired = PendingByCreature
            .Where(entry => entry.Key == null || entry.Key.HasDisposed || now - entry.Value.MarkedAt > PendingIntentLifetimeSeconds)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var creature in expired)
        {
            PendingByCreature.Remove(creature);
        }
    }

    internal readonly struct PendingIntent
    {
        public PendingIntent(ResourcePileInstance anchorPile, string anchorBlueprintId, Vector3 anchorPosition, float markedAt)
        {
            AnchorPile = anchorPile;
            AnchorBlueprintId = anchorBlueprintId;
            AnchorPosition = anchorPosition;
            MarkedAt = markedAt;
            PriorityPiles = new[] { anchorPile };
        }

        public PendingIntent(
            ResourcePileInstance anchorPile,
            string anchorBlueprintId,
            Vector3 anchorPosition,
            float markedAt,
            IReadOnlyList<ResourcePileInstance> priorityPiles)
        {
            AnchorPile = anchorPile;
            AnchorBlueprintId = anchorBlueprintId;
            AnchorPosition = anchorPosition;
            MarkedAt = markedAt;
            PriorityPiles = priorityPiles;
        }

        public ResourcePileInstance AnchorPile { get; }
        public string AnchorBlueprintId { get; }
        public Vector3 AnchorPosition { get; }
        public float MarkedAt { get; }
        public IReadOnlyList<ResourcePileInstance> PriorityPiles { get; }
    }
}

