namespace SmartHauling.Runtime.Tests;

public sealed class StockpileHaulOriginClassifierTests
{
    [Fact]
    public void Classify_ReturnsPlayerForced_WhenPendingAnchorExists()
    {
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "stew", default, 0f),
            playerForcedSourceMatches: true,
            playerForcedAnchorToSourceDistance: 0f,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 2f,
            carryAtStart: 0);

        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
    }

    [Fact]
    public void Classify_ReturnsLocalCleanup_WhenRecentGoalIsLocalProducerAndSourceIsNear()
    {
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 2.3f,
            carryAtStart: 0);

        Assert.Equal(StockpileHaulOriginCategory.LocalCleanup, classification.Category);
        Assert.Equal(RecentGoalClass.LocalProducer, classification.RecentGoalClass);
    }

    [Fact]
    public void Classify_ReturnsAutonomousHaul_WhenRecentGoalIsLocalProducerButSourceIsFar()
    {
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("ProductionCookingGoal", RecentGoalClass.LocalProducer, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 9f,
            carryAtStart: 0);

        Assert.Equal(StockpileHaulOriginCategory.AutonomousHaul, classification.Category);
    }

    [Fact]
    public void Classify_ReturnsAutonomousHaul_WhenRecentGoalIsNotLocalProducer()
    {
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: null,
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: -1f,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("StockpileHaulingGoal", RecentGoalClass.Other, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 1.5f,
            carryAtStart: 0);

        Assert.Equal(StockpileHaulOriginCategory.AutonomousHaul, classification.Category);
        Assert.Equal(RecentGoalClass.Other, classification.RecentGoalClass);
    }

    [Fact]
    public void Classify_ReturnsPlayerForced_WhenSourceIsNearPendingAnchorCluster()
    {
        var classification = StockpileHaulOriginClassifier.Classify(
            playerForcedIntent: new PlayerForcedHaulIntentStore.PendingIntent(default!, "wood", default, 0f),
            playerForcedSourceMatches: false,
            playerForcedAnchorToSourceDistance: 3f,
            recentGoal: new RecentGoalOriginStore.RecentGoalEndContext("StockpileHaulingGoal", RecentGoalClass.Other, "<none>", default, 0f, 0, "<none>"),
            agentToSourceDistance: 8f,
            carryAtStart: 0);

        Assert.Equal(StockpileHaulOriginCategory.PlayerForced, classification.Category);
    }

    [Theory]
    [InlineData("ProductionResearchGoal")]
    [InlineData("DeliverBuildingMaterialsGoal")]
    [InlineData("HarvestAnimalGoal")]
    public void IsLocalProducerGoalType_ReturnsTrue_ForKnownLocalCleanupGoals(string goalType)
    {
        var result = StockpileHaulPolicy.IsLocalProducerGoalType(goalType);

        Assert.True(result);
    }
}

