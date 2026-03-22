using System.Collections.Generic;
using NSMedieval.Goap;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class HaulFailureBackoffStore
{
    private const float BackoffSeconds = 12f;
    private const float EmptyPileBackoffSeconds = 6f;

    private static readonly Dictionary<ResourcePileInstance, float> ExpiresAtByPile =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);

    public static bool IsCoolingDown(ResourcePileInstance? pile)
    {
        if (pile == null)
        {
            return false;
        }

        Cleanup(pile);
        return ExpiresAtByPile.TryGetValue(pile, out var expiresAt) && expiresAt > Time.realtimeSinceStartup;
    }

    public static int MarkFailed(IEnumerable<ResourcePileInstance> piles)
    {
        return MarkFailed(piles, BackoffSeconds);
    }

    public static int MarkEmptyPile(IEnumerable<ResourcePileInstance> piles)
    {
        return MarkFailed(piles, EmptyPileBackoffSeconds);
    }

    private static int MarkFailed(IEnumerable<ResourcePileInstance> piles, float durationSeconds)
    {
        var marked = 0;
        var expiresAt = Time.realtimeSinceStartup + durationSeconds;
        foreach (var pile in piles)
        {
            if (pile == null || pile.HasDisposed)
            {
                continue;
            }

            ExpiresAtByPile[pile] = expiresAt;
            marked++;
        }

        return marked;
    }

    private static void Cleanup(ResourcePileInstance pile)
    {
        if (!ExpiresAtByPile.TryGetValue(pile, out var expiresAt))
        {
            return;
        }

        if (pile.HasDisposed || expiresAt <= Time.realtimeSinceStartup)
        {
            ExpiresAtByPile.Remove(pile);
        }
    }
}
