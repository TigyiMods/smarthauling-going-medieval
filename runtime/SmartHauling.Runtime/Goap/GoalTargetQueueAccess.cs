using HarmonyLib;
using NSMedieval.Goap;

namespace SmartHauling.Runtime;

internal static class GoalTargetQueueAccess
{
    private static readonly System.Reflection.MethodInfo ClearTargetsQueueMethod =
        AccessTools.Method(typeof(Goal), "ClearTargetsQueue", new[] { typeof(TargetIndex) })!;

    private static readonly System.Reflection.MethodInfo QueueTargetMethod =
        AccessTools.Method(typeof(Goal), "QueueTarget", new[] { typeof(TargetIndex), typeof(TargetObject) })!;

    public static void ClearTargetsQueue(Goal goal, TargetIndex index)
    {
        ClearTargetsQueueMethod.Invoke(goal, new object[] { index });
    }

    public static void QueueTarget(Goal goal, TargetIndex index, TargetObject target)
    {
        QueueTargetMethod.Invoke(goal, new object[] { index, target });
    }
}
