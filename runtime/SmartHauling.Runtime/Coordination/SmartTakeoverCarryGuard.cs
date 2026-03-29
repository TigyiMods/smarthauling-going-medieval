using NSMedieval.Goap;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime;

internal readonly struct SmartTakeoverCarryDecision
{
    public SmartTakeoverCarryDecision(bool shouldBlock, string reason)
    {
        ShouldBlock = shouldBlock;
        Reason = reason;
    }

    public bool ShouldBlock { get; }

    public string Reason { get; }
}

internal static class SmartTakeoverCarryGuard
{
    private const string CoordinatedPrepareDropActionId = "Instant CoordinatedHaul.PrepareDrop";
    private const float FailedPrepareDropBackoffSeconds = 1.5f;

    public static SmartTakeoverCarryDecision Evaluate(
        RecentGoalOriginStore.RecentGoalEndContext? recentGoal,
        int currentCarryCount,
        float? now = null)
    {
        if (currentCarryCount > 0)
        {
            return new SmartTakeoverCarryDecision(
                shouldBlock: true,
                reason: $"current-carry:{currentCarryCount}");
        }

        if (recentGoal.HasValue &&
            string.Equals(recentGoal.Value.GoalType, "SmartUnloadGoal", System.StringComparison.Ordinal) &&
            recentGoal.Value.Condition != GoalCondition.Succeeded &&
            recentGoal.Value.CarryCount > 0)
        {
            return new SmartTakeoverCarryDecision(
                shouldBlock: true,
                reason: $"recent-smart-unload-carry:{recentGoal.Value.CarryCount}");
        }

        if (recentGoal.HasValue &&
            recentGoal.Value.CarryCount > 0 &&
            recentGoal.Value.Condition != GoalCondition.Succeeded &&
            string.Equals(recentGoal.Value.ActionId, CoordinatedPrepareDropActionId, System.StringComparison.Ordinal))
        {
            var nowValue = now ?? RuntimeServices.Clock.RealtimeSinceStartup;
            var age = nowValue - recentGoal.Value.EndedAt;
            if (age <= FailedPrepareDropBackoffSeconds)
            {
                return new SmartTakeoverCarryDecision(
                    shouldBlock: true,
                    reason: $"recent-prepare-drop-carry:{recentGoal.Value.CarryCount}");
            }
        }

        return new SmartTakeoverCarryDecision(shouldBlock: false, reason: "allow");
    }
}
