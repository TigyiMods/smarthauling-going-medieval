using NSMedieval.Goap;

namespace SmartHauling.Runtime.Tests;

public sealed class SmartTakeoverCarryGuardTests
{
    [Fact]
    public void Evaluate_WhenCurrentCarryExists_BlocksSmartTakeover()
    {
        // Arrange
        RecentGoalOriginStore.RecentGoalEndContext? recentGoal = null;

        // Act
        var decision = SmartTakeoverCarryGuard.Evaluate(recentGoal, currentCarryCount: 12);

        // Assert
        Assert.True(decision.ShouldBlock);
        Assert.Equal("current-carry:12", decision.Reason);
    }

    [Fact]
    public void Evaluate_WhenRecentSmartUnloadFailedWithCarry_BlocksSmartTakeover()
    {
        // Arrange
        var recentGoal = new RecentGoalOriginStore.RecentGoalEndContext(
            goalType: "SmartUnloadGoal",
            recentGoalClass: RecentGoalClass.Other,
            actionId: "Instant SmartUnload.PrepareDrop",
            condition: GoalCondition.Incompletable,
            endedAt: 10f,
            carryCount: 87,
            carrySummary: "sticks:87");

        // Act
        var decision = SmartTakeoverCarryGuard.Evaluate(recentGoal, currentCarryCount: 0);

        // Assert
        Assert.True(decision.ShouldBlock);
        Assert.Equal("recent-smart-unload-carry:87", decision.Reason);
    }

    [Fact]
    public void Evaluate_WhenRecentPrepareDropFailedWithCarry_BlocksSmartTakeoverBriefly()
    {
        // Arrange
        var recentGoal = new RecentGoalOriginStore.RecentGoalEndContext(
            goalType: "StockpileHaulingGoal",
            recentGoalClass: RecentGoalClass.Other,
            actionId: "Instant CoordinatedHaul.PrepareDrop",
            condition: GoalCondition.Incompletable,
            endedAt: 10f,
            carryCount: 49,
            carrySummary: "hay:49");

        // Act
        var decision = SmartTakeoverCarryGuard.Evaluate(recentGoal, currentCarryCount: 0, now: 10.8f);

        // Assert
        Assert.True(decision.ShouldBlock);
        Assert.Equal("recent-prepare-drop-carry:49", decision.Reason);
    }

    [Fact]
    public void Evaluate_WhenRecentPrepareDropFailureIsOld_AllowsSmartTakeover()
    {
        // Arrange
        var recentGoal = new RecentGoalOriginStore.RecentGoalEndContext(
            goalType: "StockpileHaulingGoal",
            recentGoalClass: RecentGoalClass.Other,
            actionId: "Instant CoordinatedHaul.PrepareDrop",
            condition: GoalCondition.Incompletable,
            endedAt: 10f,
            carryCount: 49,
            carrySummary: "hay:49");

        // Act
        var decision = SmartTakeoverCarryGuard.Evaluate(recentGoal, currentCarryCount: 0, now: 11.7f);

        // Assert
        Assert.False(decision.ShouldBlock);
        Assert.Equal("allow", decision.Reason);
    }

    [Fact]
    public void Evaluate_WhenNoResidualCarry_AllowsSmartTakeover()
    {
        // Arrange
        var recentGoal = new RecentGoalOriginStore.RecentGoalEndContext(
            goalType: "FaithGoal",
            recentGoalClass: RecentGoalClass.Other,
            actionId: "<none>",
            condition: GoalCondition.Succeeded,
            endedAt: 10f,
            carryCount: 0,
            carrySummary: "<empty>");

        // Act
        var decision = SmartTakeoverCarryGuard.Evaluate(recentGoal, currentCarryCount: 0);

        // Assert
        Assert.False(decision.ShouldBlock);
        Assert.Equal("allow", decision.Reason);
    }
}
