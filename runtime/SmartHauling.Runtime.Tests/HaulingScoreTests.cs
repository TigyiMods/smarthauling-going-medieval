using SmartHauling.Runtime;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingScoreTests
{
    [Fact]
    public void CalculateBoardAssignmentScore_AddsFullBonusAtZeroDistance()
    {
        var score = HaulingScore.CalculateBoardAssignmentScore(100f, 0f);

        Assert.Equal(148f, score, 3);
    }

    [Fact]
    public void CalculateBoardAssignmentScore_ClampsBonusToZeroForFarTargets()
    {
        var score = HaulingScore.CalculateBoardAssignmentScore(100f, 100f);

        Assert.Equal(100f, score, 3);
    }

    [Fact]
    public void CalculateMaterializedSelectionScore_RewardsBetterFill()
    {
        var lowFill = HaulingScore.CalculateMaterializedSelectionScore(
            pickupBudget: 20,
            requestedAmount: 100,
            estimatedPileCount: 3,
            estimatedResourceTypes: 2);
        var highFill = HaulingScore.CalculateMaterializedSelectionScore(
            pickupBudget: 80,
            requestedAmount: 100,
            estimatedPileCount: 3,
            estimatedResourceTypes: 2);

        Assert.True(highFill > lowFill);
    }

    [Fact]
    public void CalculateTaskSeedScore_AddsSaplingBonus()
    {
        var regular = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal: 120,
            patchExtent: 6f,
            estimatedResourceTypes: 2,
            estimatedPileCount: 4,
            isSapling: false);
        var sapling = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal: 120,
            patchExtent: 6f,
            estimatedResourceTypes: 2,
            estimatedPileCount: 4,
            isSapling: true);

        Assert.Equal(60f, sapling - regular, 3);
    }
}
