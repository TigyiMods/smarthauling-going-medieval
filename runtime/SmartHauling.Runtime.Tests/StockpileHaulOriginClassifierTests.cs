namespace SmartHauling.Runtime.Tests;

public sealed class StockpileHaulOriginClassifierTests
{
    [Fact]
    public void Classify_ReturnsPlayerForced_WhenPendingAnchorExists()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "stew", default, 0f),
            playerForcedSourceMatches: true,
            playerForcedAnchorToSourceDistance: 0f,
            isUrgentPriorityHaul: false,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 2f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
        Assert.Equal("player-forced-priority-match", classification.Reason);
    }

    [Fact]
    public void Classify_ReturnsLocalCleanup_WhenRecentGoalIsLocalProducerAndSourceIsNear()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            isUrgentPriorityHaul: false,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 2.3f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.LocalCleanup, classification.Category);
        Assert.Equal(RecentGoalClass.LocalProducer, classification.RecentGoalClass);
    }

    [Fact]
    public void Classify_ReturnsAutonomousHaul_WhenRecentGoalIsLocalProducerButSourceIsFar()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            isUrgentPriorityHaul: false,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 9f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.AutonomousHaul, classification.Category);
    }

    [Fact]
    public void Classify_ReturnsAutonomousHaul_WhenRecentGoalIsNotLocalProducer()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            isUrgentPriorityHaul: false,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("StockpileHaulingGoal", RecentGoalClass.Other, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 1.5f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.AutonomousHaul, classification.Category);
        Assert.Equal(RecentGoalClass.Other, classification.RecentGoalClass);
    }

    [Fact]
    public void Classify_ReturnsPlayerForced_WhenSourceIsNearPendingAnchorCluster()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "wood", default, 0f),
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: 3f,
            isUrgentPriorityHaul: false,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("StockpileHaulingGoal", RecentGoalClass.Other, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 8f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
    }

    [Fact]
    public void Classify_ReturnsUrgentPriorityReason_WhenUrgentManualHaulMatches()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "sticks", default, 0f),
            playerForcedSourceMatches: true,
            playerForcedAnchorToSourceDistance: 0f,
            isUrgentPriorityHaul: true,
            recentGoal: null,
            agentToSourceDistance: 4f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
        Assert.Equal("urgent-priority-match", classification.Reason);
    }

    [Fact]
    public void Classify_ReturnsUrgentAnchorOverride_WhenUrgentIntentNeedsReseed()
    {
        // Arrange
        // Act
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "oak_sapling", default, 0f),
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: 12f,
            isUrgentPriorityHaul: true,
            recentGoal: null,
            agentToSourceDistance: 12f,
            carryAtStart: 0);

        // Assert
        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
        Assert.Equal("urgent-anchor-override", classification.Reason);
    }

    [Theory]
    [InlineData("ProductionResearchGoal")]
    [InlineData("DeliverBuildingMaterialsGoal")]
    [InlineData("HarvestAnimalGoal")]
    public void IsLocalProducerGoalType_ReturnsTrue_ForKnownLocalCleanupGoals(string goalType)
    {
        // Act
        var result = StockpileHaulPolicy.IsLocalProducerGoalType(goalType);

        // Assert
        Assert.True(result);
    }
}

