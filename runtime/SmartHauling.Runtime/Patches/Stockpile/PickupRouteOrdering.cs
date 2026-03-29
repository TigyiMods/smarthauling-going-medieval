using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal static class PickupRouteOrdering
{
    public static IReadOnlyList<ResourcePileInstance> OrderCandidates(
        IEnumerable<ResourcePileInstance> candidates,
        Vector3 startPosition,
        Vector3? targetPosition)
    {
        var candidateList = candidates
            .Where(candidate => candidate != null && !candidate.HasDisposed)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList();
        return RouteOrderingOptimizer.OrderOptimal(
            candidateList,
            startPosition,
            candidate => candidate.GetPosition(),
            (currentPosition, candidate) => GetCandidateScore(currentPosition, candidate.GetPosition(), targetPosition),
            candidate => GetCandidateScore(startPosition, candidate.GetPosition(), targetPosition));
    }

    private static float GetCandidateScore(Vector3 currentPosition, Vector3 candidatePosition, Vector3? targetPosition)
    {
        if (!targetPosition.HasValue)
        {
            return Vector3.Distance(currentPosition, candidatePosition);
        }

        return HaulGeometry.GetAdditionalDetour(
            currentPosition,
            candidatePosition,
            targetPosition.Value);
    }
}
