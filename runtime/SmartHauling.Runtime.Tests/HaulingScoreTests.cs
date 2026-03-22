using SmartHauling.Runtime;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingScoreTests
{
    [Fact]
    public void CalculateBoardAssignmentScore_WhenDistanceIsZero_ReturnsMaximumLocalityBonus()
    {
        // Arrange
        const float seedScore = 100f;
        const float distance = 0f;

        // Act
        var score = HaulingScore.CalculateBoardAssignmentScore(seedScore, distance);

        // Assert
        Assert.Equal(148f, score, 3);
    }

    [Fact]
    public void CalculateBoardAssignmentScore_WhenTargetIsFarAway_ClampsLocalityBonusToZero()
    {
        // Arrange
        const float seedScore = 100f;
        const float distance = 100f;

        // Act
        var score = HaulingScore.CalculateBoardAssignmentScore(seedScore, distance);

        // Assert
        Assert.Equal(100f, score, 3);
    }

    [Fact]
    public void CalculateMaterializedSelectionScore_WhenFillImproves_ReturnsHigherScore()
    {
        // Arrange
        var lowFillScore = HaulingScore.CalculateMaterializedSelectionScore(
            pickupBudget: 20,
            requestedAmount: 100,
            estimatedPileCount: 3,
            estimatedResourceTypes: 2);
        var highFillScore = HaulingScore.CalculateMaterializedSelectionScore(
            pickupBudget: 80,
            requestedAmount: 100,
            estimatedPileCount: 3,
            estimatedResourceTypes: 2);

        // Act
        var scoreDelta = highFillScore - lowFillScore;

        // Assert
        Assert.True(scoreDelta > 0f);
    }

    [Fact]
    public void CalculateTaskSeedScore_WhenResourceIsSapling_AddsSaplingBonus()
    {
        // Arrange
        var regularScore = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal: 120,
            patchExtent: 6f,
            estimatedResourceTypes: 2,
            estimatedPileCount: 4,
            isSapling: false);
        var saplingScore = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal: 120,
            patchExtent: 6f,
            estimatedResourceTypes: 2,
            estimatedPileCount: 4,
            isSapling: true);

        // Act
        var saplingBonus = saplingScore - regularScore;

        // Assert
        Assert.Equal(60f, saplingBonus, 3);
    }
}
