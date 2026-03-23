using System.Runtime.CompilerServices;
using NSMedieval.Goap;
using NSMedieval.State;

namespace SmartHauling.Runtime;

internal static class GoalSourcePriorityStore
{
    private sealed class GoalSourcePriority
    {
        public GoalSourcePriority(ZonePriority value)
        {
            Value = value;
        }

        public ZonePriority Value { get; }
    }

    private static readonly ConditionalWeakTable<Goal, GoalSourcePriority> SourcePriorityByGoal = new();

    public static void Set(Goal goal, ZonePriority sourcePriority)
    {
        if (goal == null)
        {
            return;
        }

        Clear(goal);
        SourcePriorityByGoal.Add(goal, new GoalSourcePriority(sourcePriority));
    }

    public static bool TryGet(Goal goal, out ZonePriority sourcePriority)
    {
        if (goal != null && SourcePriorityByGoal.TryGetValue(goal, out var value))
        {
            sourcePriority = value.Value;
            return true;
        }

        sourcePriority = ZonePriority.None;
        return false;
    }

    public static void Clear(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        SourcePriorityByGoal.Remove(goal);
    }
}
