using NSMedieval.Goap;

namespace SmartHauling.Runtime.Tests;

public sealed class RecentGoalOriginStoreTests
{
    [Fact]
    public void NormalizeRecordedCondition_WhenSmartUnloadDoneHasNoCarry_CoercesSucceeded()
    {
        // Arrange
        const string goalType = "SmartUnloadGoal";
        const string actionId = "Instant SmartUnload.Done";

        // Act
        var condition = RecentGoalOriginStore.NormalizeRecordedCondition(
            goalType,
            actionId,
            GoalCondition.Incompletable,
            carryCount: 0);

        // Assert
        Assert.Equal(GoalCondition.Succeeded, condition);
    }

    [Fact]
    public void NormalizeRecordedCondition_WhenSmartUnloadDoneStillHasCarry_PreservesCondition()
    {
        // Arrange
        const string goalType = "SmartUnloadGoal";
        const string actionId = "Instant SmartUnload.Done";

        // Act
        var condition = RecentGoalOriginStore.NormalizeRecordedCondition(
            goalType,
            actionId,
            GoalCondition.Incompletable,
            carryCount: 3);

        // Assert
        Assert.Equal(GoalCondition.Incompletable, condition);
    }

    [Fact]
    public void NormalizeRecordedCondition_WhenDifferentGoal_PreservesCondition()
    {
        // Arrange
        const string goalType = "StockpileHaulingGoal";
        const string actionId = "Instant CoordinatedHaul.Done";

        // Act
        var condition = RecentGoalOriginStore.NormalizeRecordedCondition(
            goalType,
            actionId,
            GoalCondition.Incompletable,
            carryCount: 0);

        // Assert
        Assert.Equal(GoalCondition.Incompletable, condition);
    }
}
