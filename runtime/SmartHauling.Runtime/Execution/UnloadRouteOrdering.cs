using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class UnloadRouteOrdering
{
    public static IReadOnlyList<T> OrderCandidates<T>(
        IEnumerable<T> candidates,
        Vector3 startPosition,
        Func<T, int> getPriorityRank,
        Func<T, ZonePriority> getTargetPriority,
        Func<T, Vector3?> getAnchorPosition,
        Func<T, float> getNearestDistance,
        Func<T, int> getAmount)
    {
        var ordered = new List<T>();
        var currentPosition = startPosition;

        // Route-first within the highest feasible target-priority bands.
        // Highest target priority wins first, then route optimization from current position.
        foreach (var targetPriorityGroup in candidates
                     .GroupBy(getTargetPriority)
                     .OrderByDescending(group => group.Key))
        {
            foreach (var priorityGroup in targetPriorityGroup
                         .GroupBy(getPriorityRank)
                         .OrderBy(group => group.Key))
            {
                var orderedWithinBand = RouteOrderingOptimizer.OrderOptimal(
                    priorityGroup.ToList(),
                    currentPosition,
                    getAnchorPosition,
                    (position, candidate) => GetTravelDistance(position, getAnchorPosition(candidate), getNearestDistance(candidate)),
                    candidate => getNearestDistance(candidate) - (getAmount(candidate) * 0.01f));
                ordered.AddRange(orderedWithinBand);
                var lastAnchored = orderedWithinBand
                    .Select(getAnchorPosition)
                    .LastOrDefault(anchor => anchor.HasValue);
                if (lastAnchored.HasValue)
                {
                    currentPosition = lastAnchored.Value;
                }
            }
        }

        return ordered;
    }

    private static float GetTravelDistance(Vector3 currentPosition, Vector3? anchorPosition, float fallbackDistance)
    {
        return anchorPosition.HasValue
            ? Vector3.Distance(currentPosition, anchorPosition.Value)
            : fallbackDistance;
    }
}
