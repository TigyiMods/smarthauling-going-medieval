using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class HaulingScore
{
    private const float PriorityRankBonusPerStep = 18f;
    private const float ReprioritizationBonusPerStep = 28f;

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

    public static float CalculateBoardAssignmentScore(
        float baseScore,
        float distanceToSource,
        ZonePriority sourcePriority,
        ZonePriority targetPriority)
    {
        var proximityBonus = Mathf.Max(0f, 48f - (distanceToSource * 1.5f));
        var targetPriorityBonus = GetPriorityRank(targetPriority) * PriorityRankBonusPerStep;
        var reprioritizationBonus = Mathf.Max(0, GetPriorityRank(targetPriority) - GetPriorityRank(sourcePriority)) * ReprioritizationBonusPerStep;
        return baseScore + proximityBonus + targetPriorityBonus + reprioritizationBonus;
    }

    public static float CalculateBoardAssignmentScore(float baseScore, float distanceToSource)
    {
        return CalculateBoardAssignmentScore(baseScore, distanceToSource, ZonePriority.None, ZonePriority.None);
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

    private static int GetPriorityRank(ZonePriority priority)
    {
        return priority switch
        {
            ZonePriority.Low => 1,
            ZonePriority.Medium => 2,
            ZonePriority.High => 3,
            ZonePriority.VeryHigh => 4,
            ZonePriority.Last => 5,
            _ => 0
        };
    }
}
