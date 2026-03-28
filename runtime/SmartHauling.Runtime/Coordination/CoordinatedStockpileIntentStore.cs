using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime;

/// <summary>
/// Tracks short-lived worker intent for stockpile goals that were explicitly scheduled by SmartHauling.
/// </summary>
/// <remarks>
/// The ownership boundary is origin-based: only stockpile hauling goals created from the idle board trigger
/// may be taken over by the coordinated planner/executor. If this proves too conservative, relax the trigger
/// conditions upstream instead of widening downstream goal-type patches again.
/// </remarks>
internal static class CoordinatedStockpileIntentStore
{
    private const float PendingIntentLifetimeSeconds = 1f;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<CreatureBase, float> PendingUntilByCreature =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);

    public static void MarkPending(CreatureBase creature)
    {
        if (creature == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            CleanupExpired();
            PendingUntilByCreature[creature] = RuntimeServices.Clock.RealtimeSinceStartup + PendingIntentLifetimeSeconds;
        }
    }

    public static bool TryConsume(CreatureBase creature)
    {
        if (creature == null)
        {
            return false;
        }

        lock (SyncRoot)
        {
            CleanupExpired();
            if (!PendingUntilByCreature.Remove(creature))
            {
                return false;
            }

            return true;
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
            PendingUntilByCreature.Remove(creature);
        }
    }

    private static void CleanupExpired()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var expired = PendingUntilByCreature
            .Where(entry => entry.Key == null || entry.Key.HasDisposed || entry.Value <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var creature in expired)
        {
            PendingUntilByCreature.Remove(creature);
        }
    }
}
