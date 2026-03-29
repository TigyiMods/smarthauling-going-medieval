using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class CoordinatedPickupRouteOrdering
{
    public static IReadOnlyList<TPickup> OrderCandidates<TPickup>(
        IReadOnlyList<TPickup> candidates,
        Func<TPickup, string> getResourceId,
        Func<TPickup, Vector3> getPosition,
        IReadOnlyDictionary<string, IReadOnlyList<Vector3>> dropAnchorsByResourceId)
    {
        if (candidates == null || candidates.Count <= 1)
        {
            return candidates ?? Array.Empty<TPickup>();
        }

        // Keep the first planned pickup fixed. Upstream planner intentionally seeds it.
        var ordered = new List<TPickup> { candidates[0] };
        var orderedRest = RouteOrderingOptimizer.OrderOptimal(
            candidates.Skip(1).ToList(),
            getPosition(candidates[0]),
            item => getPosition(item),
            (currentPosition, item) => GetRouteScore(
                currentPosition,
                getPosition(item),
                dropAnchorsByResourceId,
                getResourceId(item)),
            item => GetAnchorDistance(
                getPosition(item),
                dropAnchorsByResourceId,
                getResourceId(item)));
        ordered.AddRange(orderedRest);
        return ordered;
    }

    public static IReadOnlyList<ResourcePileInstance> OrderPlannedPickups(
        IReadOnlyList<ResourcePileInstance> plannedPickups,
        IReadOnlyDictionary<string, IReadOnlyList<Vector3>> dropAnchorsByResourceId)
    {
        var activePickups = plannedPickups?
            .Where(pile => pile != null && !pile.HasDisposed)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList()
            ?? new List<ResourcePileInstance>();

        return OrderCandidates(
            activePickups,
            pile => pile.BlueprintId,
            pile => pile.GetPosition(),
            dropAnchorsByResourceId);
    }

    public static IReadOnlyList<string> BuildDropOrder<TPickup>(
        string primaryResourceId,
        IReadOnlyList<TPickup> orderedPickups,
        Func<TPickup, string> getResourceId,
        IEnumerable<string> allResourceIds)
    {
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryResourceId))
        {
            ordered.Add(primaryResourceId);
        }

        foreach (var resourceId in orderedPickups
                     .Select(getResourceId)
                     .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
                     .Distinct())
        {
            if (!ordered.Contains(resourceId))
            {
                ordered.Add(resourceId);
            }
        }

        foreach (var resourceId in allResourceIds
                     .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
                     .Distinct())
        {
            if (!ordered.Contains(resourceId))
            {
                ordered.Add(resourceId);
            }
        }

        return ordered;
    }

    private static float GetRouteScore(
        Vector3 currentPosition,
        Vector3 candidatePosition,
        IReadOnlyDictionary<string, IReadOnlyList<Vector3>> dropAnchorsByResourceId,
        string resourceId)
    {
        if (dropAnchorsByResourceId == null ||
            string.IsNullOrWhiteSpace(resourceId) ||
            !dropAnchorsByResourceId.TryGetValue(resourceId, out var anchors) ||
            anchors == null ||
            anchors.Count == 0)
        {
            return Vector3.Distance(currentPosition, candidatePosition);
        }

        return anchors
            .Select(anchor => HaulGeometry.GetAdditionalDetour(currentPosition, candidatePosition, anchor))
            .DefaultIfEmpty(Vector3.Distance(currentPosition, candidatePosition))
            .Min();
    }

    private static float GetAnchorDistance(
        Vector3 candidatePosition,
        IReadOnlyDictionary<string, IReadOnlyList<Vector3>> dropAnchorsByResourceId,
        string resourceId)
    {
        if (dropAnchorsByResourceId == null ||
            string.IsNullOrWhiteSpace(resourceId) ||
            !dropAnchorsByResourceId.TryGetValue(resourceId, out var anchors) ||
            anchors == null ||
            anchors.Count == 0)
        {
            return float.MaxValue;
        }

        return anchors
            .Select(anchor => Vector3.Distance(candidatePosition, anchor))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
    }
}
