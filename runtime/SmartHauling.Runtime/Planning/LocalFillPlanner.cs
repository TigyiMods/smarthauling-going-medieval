using UnityEngine;

namespace SmartHauling.Runtime;

internal static class LocalFillPlanner
{
    public static float GetAnchorDistance(
        IEnumerable<Vector3> anchorPositions,
        Vector3? fallbackAnchorPosition,
        Vector3 candidatePosition)
    {
        var nearest = float.MaxValue;
        if (anchorPositions != null)
        {
            foreach (var anchorPosition in anchorPositions)
            {
                var distance = Vector3.Distance(anchorPosition, candidatePosition);
                if (distance < nearest)
                {
                    nearest = distance;
                }
            }
        }

        if (fallbackAnchorPosition.HasValue)
        {
            var distance = Vector3.Distance(fallbackAnchorPosition.Value, candidatePosition);
            if (distance < nearest)
            {
                nearest = distance;
            }
        }

        return nearest < float.MaxValue ? nearest : 0f;
    }

    public static float CalculateCandidateScore(
        int requestedAmount,
        float distance,
        float patchDistance,
        bool hasExistingDropPlan)
    {
        var cappedAmount = Mathf.Min(requestedAmount, 16);
        var amountScore = cappedAmount * 3f;
        var existingPlanBonus = hasExistingDropPlan ? 24f : 0f;
        var localPatchBonus = Mathf.Max(0f, 72f - (patchDistance * 12f));
        var distancePenalty = distance * 4f;
        return amountScore + existingPlanBonus + localPatchBonus - distancePenalty;
    }
}
