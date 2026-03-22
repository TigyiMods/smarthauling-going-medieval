using UnityEngine;

namespace SmartHauling.Runtime;

internal static class HaulingScore
{
    public static float CalculateMaterializedSelectionScore(
        int pickupBudget,
        int requestedAmount,
        int estimatedPileCount,
        int estimatedResourceTypes)
    {
        var requestFit = requestedAmount <= 0
            ? 1f
            : Mathf.Clamp01((float)pickupBudget / requestedAmount);
        var fillScore = Mathf.Sqrt(Mathf.Max(1, pickupBudget)) * 90f;
        var fitScore = requestFit * 120f;
        var pileScore = Mathf.Min(estimatedPileCount, 12) * 6f;
        var diversityScore = Mathf.Min(estimatedResourceTypes, 4) * 14f;
        return fillScore + fitScore + pileScore + diversityScore;
    }

    public static float CalculateBoardAssignmentScore(float baseScore, float distanceToSource)
    {
        var proximityBonus = Mathf.Max(0f, 48f - (distanceToSource * 1.5f));
        return baseScore + proximityBonus;
    }

    public static float CalculateTaskSeedScore(
        int estimatedTotal,
        float patchExtent,
        int estimatedResourceTypes,
        int estimatedPileCount,
        bool isSapling)
    {
        var amountScore = Mathf.Sqrt(Mathf.Max(1, estimatedTotal)) * 42f;
        var densityScore = (estimatedTotal / Mathf.Max(6f, patchExtent)) * 8f;
        var diversityBonus = estimatedResourceTypes * 70f;
        var pileBonus = estimatedPileCount * 12f;
        var patchBonus = isSapling ? 60f : 0f;
        return amountScore +
               densityScore +
               diversityBonus +
               pileBonus +
               patchBonus;
    }
}
