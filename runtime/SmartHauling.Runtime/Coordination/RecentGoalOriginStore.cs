using NSMedieval.Goap;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime;

internal static class RecentGoalOriginStore
{
    private const float RecentWindowSeconds = 5f;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<CreatureBase, RecentGoalEndContext> RecentByCreature =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);

    public static void RecordGoalEnd(CreatureBase creature, Goal goal, GoalCondition condition)
    {
        if (creature == null || goal == null)
        {
            return;
        }

        var carryCount = creature is IStorageAgent { Storage: not null } storageAgent
            ? storageAgent.Storage.GetTotalStoredCount()
            : 0;
        var carrySummary = creature is IStorageAgent { Storage: not null } summaryAgent
            ? CarrySummaryUtil.Summarize(summaryAgent.Storage)
            : "<no-storage>";

        lock (SyncRoot)
        {
            CleanupExpired();
            RecentByCreature[creature] = new RecentGoalEndContext(
                goal.GetType().Name,
                StockpileHaulPolicy.ClassifyRecentGoal(goal),
                goal.CurrentAction?.Id ?? "<none>",
                condition,
                RuntimeServices.Clock.RealtimeSinceStartup,
                carryCount,
                carrySummary);
        }
    }

    public static bool TryGetRecent(CreatureBase creature, out RecentGoalEndContext context)
    {
        if (creature == null)
        {
            context = default;
            return false;
        }

        lock (SyncRoot)
        {
            CleanupExpired();
            return RecentByCreature.TryGetValue(creature, out context);
        }
    }

    private static void CleanupExpired()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var expired = RecentByCreature
            .Where(entry => entry.Key == null || entry.Key.HasDisposed || now - entry.Value.EndedAt > RecentWindowSeconds)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var creature in expired)
        {
            RecentByCreature.Remove(creature);
        }
    }

    internal readonly struct RecentGoalEndContext
    {
        public RecentGoalEndContext(
            string goalType,
            RecentGoalClass recentGoalClass,
            string actionId,
            GoalCondition condition,
            float endedAt,
            int carryCount,
            string carrySummary)
        {
            GoalType = goalType;
            RecentGoalClass = recentGoalClass;
            ActionId = actionId;
            Condition = condition;
            EndedAt = endedAt;
            CarryCount = carryCount;
            CarrySummary = carrySummary;
        }

        public string GoalType { get; }
        public RecentGoalClass RecentGoalClass { get; }
        public string ActionId { get; }
        public GoalCondition Condition { get; }
        public float EndedAt { get; }
        public int CarryCount { get; }
        public string CarrySummary { get; }
    }
}

