using NSMedieval.State;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingScoreTests
{
    [Fact]
    public void CalculateBoardAssignmentScore_WhenTargetPriorityIsHigher_BoostsSelection()
    {
        // Arrange
        const float baseScore = 100f;
        const float distanceToSource = 10f;

        // Act
        var mediumScore = HaulingScore.CalculateBoardAssignmentScore(baseScore, distanceToSource, ZonePriority.None, ZonePriority.Medium);
        var veryHighScore = HaulingScore.CalculateBoardAssignmentScore(baseScore, distanceToSource, ZonePriority.None, ZonePriority.VeryHigh);

        // Assert
        Assert.True(veryHighScore > mediumScore);
    }

    [Fact]
    public void CalculateBoardAssignmentScore_WhenReprioritizing_BoostsPromotionTargets()
    {
        // Arrange
        const float baseScore = 100f;
        const float distanceToSource = 10f;

        // Act
        var lowToMedium = HaulingScore.CalculateBoardAssignmentScore(baseScore, distanceToSource, ZonePriority.Low, ZonePriority.Medium);
        var lowToVeryHigh = HaulingScore.CalculateBoardAssignmentScore(baseScore, distanceToSource, ZonePriority.Low, ZonePriority.VeryHigh);

        // Assert
        Assert.True(lowToVeryHigh > lowToMedium);
    }
}
