using System.Collections.Generic;
using NSMedieval.Goap;

namespace SmartHauling.Runtime;

internal static class StorageEmptyRecoveryStore
{
    private const int MaxRetriesPerGoal = 6;

    private static readonly Dictionary<Goal, int> RetryCounts =
        new(ReferenceEqualityComparer<Goal>.Instance);

    public static bool TryConsumeRetry(Goal goal)
    {
        if (goal == null)
        {
            return false;
        }

        var current = RetryCounts.TryGetValue(goal, out var value) ? value : 0;
        if (current >= MaxRetriesPerGoal)
        {
            return false;
        }

        RetryCounts[goal] = current + 1;
        return true;
    }

    public static int GetRetryCount(Goal goal)
    {
        return goal != null && RetryCounts.TryGetValue(goal, out var value) ? value : 0;
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        RetryCounts.Remove(goal);
    }
}
