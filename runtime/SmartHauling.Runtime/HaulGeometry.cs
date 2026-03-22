using System.Collections.Generic;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class HaulGeometry
{
    public static float GetNearestPatchDistance(IEnumerable<Vector3> patchPositions, Vector3 candidatePosition)
    {
        var nearest = float.MaxValue;
        foreach (var patchPosition in patchPositions)
        {
            var distance = Vector3.Distance(patchPosition, candidatePosition);
            if (distance < nearest)
            {
                nearest = distance;
            }
        }

        return nearest;
    }

    public static float GetPatchExtent(Vector3 firstPosition, IEnumerable<Vector3> positions)
    {
        var maxDistance = 0f;
        foreach (var position in positions)
        {
            var distance = Vector3.Distance(firstPosition, position);
            if (distance > maxDistance)
            {
                maxDistance = distance;
            }
        }

        return Mathf.Max(1f, maxDistance);
    }

    public static float GetDetourBudget(
        float sourceToTargetDistance,
        float sourceClusterExtent,
        float detourBudgetMultiplier,
        float minimumDetourBudget,
        float maximumDetourBudget)
    {
        if (sourceToTargetDistance <= 0f)
        {
            return sourceClusterExtent;
        }

        return Mathf.Clamp(
            sourceToTargetDistance * detourBudgetMultiplier,
            minimumDetourBudget,
            maximumDetourBudget);
    }

    public static bool ShouldUsePatchSweep(
        string blueprintId,
        int firstPileAmount,
        int sameTypeCount,
        int patchSweepAmountThreshold,
        int patchSweepCountThreshold)
    {
        if (!string.IsNullOrWhiteSpace(blueprintId) &&
            blueprintId.Contains("sapling", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return firstPileAmount <= patchSweepAmountThreshold &&
               sameTypeCount >= patchSweepCountThreshold;
    }

    public static bool IsRouteWorthwhile(
        Vector3 firstPosition,
        Vector3 candidatePosition,
        Vector3? targetPosition,
        float detourBudget,
        float sourceClusterExtent,
        out float detourCost)
    {
        if (!targetPosition.HasValue)
        {
            detourCost = Vector3.Distance(firstPosition, candidatePosition);
            return detourCost <= sourceClusterExtent;
        }

        detourCost = GetAdditionalDetour(firstPosition, candidatePosition, targetPosition.Value);
        return detourCost <= detourBudget;
    }

    public static float GetAdditionalDetour(
        Vector3 firstPosition,
        Vector3 candidatePosition,
        Vector3 targetPosition)
    {
        var direct = Vector3.Distance(firstPosition, targetPosition);
        var viaCandidate = Vector3.Distance(firstPosition, candidatePosition) +
                           Vector3.Distance(candidatePosition, targetPosition);
        return Mathf.Max(0f, viaCandidate - direct);
    }
}
