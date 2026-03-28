namespace SmartHauling.Runtime;

internal static class StockpileHaulOriginClassifier
{
    public static StockpileHaulOriginClassification Classify(
        PlayerForcedHaulIntentStore.PendingIntent? playerForcedIntent,
        bool playerForcedSourceMatches,
        float playerForcedAnchorToSourceDistance,
        RecentGoalOriginStore.RecentGoalEndContext? recentGoal,
        float agentToSourceDistance,
        int carryAtStart)
    {
        if (playerForcedIntent.HasValue &&
            (playerForcedSourceMatches ||
             (playerForcedAnchorToSourceDistance >= 0f &&
              playerForcedAnchorToSourceDistance <= StockpileHaulPolicy.PlayerForcedSourceClusterExtent)))
        {
            return new StockpileHaulOriginClassification(
                StockpileHaulOriginCategory.PlayerForced,
                playerForcedSourceMatches
                    ? "player-forced-anchor-match"
                    : $"player-forced-local-cluster<={StockpileHaulPolicy.PlayerForcedSourceClusterExtent:0.0}",
                recentGoal?.RecentGoalClass ?? RecentGoalClass.None);
        }

        if (!recentGoal.HasValue)
        {
            return new StockpileHaulOriginClassification(
                StockpileHaulOriginCategory.AutonomousHaul,
                "no-recent-goal",
                RecentGoalClass.None);
        }

        var recentClass = recentGoal.Value.RecentGoalClass;
        if (recentClass == RecentGoalClass.LocalProducer &&
            carryAtStart <= 0 &&
            agentToSourceDistance >= 0f &&
            agentToSourceDistance <= StockpileHaulPolicy.LocalCleanupSourceDistanceThreshold)
        {
            return new StockpileHaulOriginClassification(
                StockpileHaulOriginCategory.LocalCleanup,
                $"recent-local-producer-near-source<={StockpileHaulPolicy.LocalCleanupSourceDistanceThreshold:0.0}",
                recentClass);
        }

        return new StockpileHaulOriginClassification(
            StockpileHaulOriginCategory.AutonomousHaul,
            $"recent={recentClass}, carryStart={carryAtStart}, dist={agentToSourceDistance:0.0}",
            recentClass);
    }
}

internal readonly struct StockpileHaulOriginClassification
{
    public StockpileHaulOriginClassification(StockpileHaulOriginCategory category, string reason, RecentGoalClass recentGoalClass)
    {
        Category = category;
        Reason = reason;
        RecentGoalClass = recentGoalClass;
    }

    public StockpileHaulOriginCategory Category { get; }
    public string Reason { get; }
    public RecentGoalClass RecentGoalClass { get; }
}

internal enum StockpileHaulOriginCategory
{
    PlayerForced,
    LocalCleanup,
    AutonomousHaul
}

internal enum RecentGoalClass
{
    None,
    LocalProducer,
    Other
}

