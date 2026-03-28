using NSMedieval.Goap;
using NSMedieval.Goap.Goals;

namespace SmartHauling.Runtime;

internal static class StockpileHaulPolicy
{
    internal const float SourceClusterExtent = 36f;
    internal const float PatchSweepExtent = 24f;
    internal const float PatchSweepLinkExtent = 10f;
    internal const float MixedGroundHarvestExtent = 12f;
    internal const float PlayerForcedSourceClusterExtent = 6f;
    internal const float LocalCleanupSourceDistanceThreshold = 4f;
    internal const int PatchSweepCountThreshold = 4;
    internal const int PatchSweepAmountThreshold = 3;
    internal const float MinimumDetourBudget = 12f;
    internal const float MaximumDetourBudget = 48f;
    internal const float DetourBudgetMultiplier = 0.65f;
    internal const float MinimumSourceSliceWeightBudget = 32f;

    private static readonly HashSet<string> LocalProducerGoalTypes = new(StringComparer.Ordinal)
    {
        "ConstructBuildingGoal",
        "DeliverBuildingMaterialsGoal",
        "ChopTreeGoal",
        "HarvestAnimalGoal",
        "HarvestGoal",
        "FishingGoal",
        "PlantCropsGoal",
        "DeconstructGoal"
    };

    internal static bool IsLocalProducerGoalType(string? goalType)
    {
        if (string.IsNullOrWhiteSpace(goalType))
        {
            return false;
        }

        return goalType.StartsWith("Production", StringComparison.Ordinal) ||
               LocalProducerGoalTypes.Contains(goalType);
    }

    internal static RecentGoalClass ClassifyRecentGoal(Goal? goal)
    {
        if (goal == null)
        {
            return RecentGoalClass.None;
        }

        if (goal is ProductionBaseGoal)
        {
            return RecentGoalClass.LocalProducer;
        }

        return IsLocalProducerGoalType(goal.GetType().Name)
            ? RecentGoalClass.LocalProducer
            : RecentGoalClass.Other;
    }
}
