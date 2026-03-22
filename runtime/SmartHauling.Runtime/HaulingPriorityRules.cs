using System.Linq;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using SmartHauling.Runtime.Goals;

namespace SmartHauling.Runtime;

internal static class HaulingPriorityRules
{
    public static bool CanMoveToPriority(ZonePriority sourcePriority, ZonePriority targetPriority)
    {
        return sourcePriority == ZonePriority.None || targetPriority > sourcePriority;
    }

    public static ZonePriority GetRequiredMinimumPriority(ZonePriority sourcePriority, ZonePriority requestedMinimumPriority)
    {
        if (sourcePriority == ZonePriority.None)
        {
            return requestedMinimumPriority;
        }

        var promotedPriority = sourcePriority switch
        {
            ZonePriority.Low => ZonePriority.Medium,
            ZonePriority.Medium => ZonePriority.High,
            ZonePriority.High => ZonePriority.VeryHigh,
            ZonePriority.VeryHigh => ZonePriority.Last,
            _ => requestedMinimumPriority
        };

        return (ZonePriority)System.Math.Max((int)requestedMinimumPriority, (int)promotedPriority);
    }

    public static bool TryGetGoalSourcePriority(Goal goal, CreatureBase creature, out ZonePriority sourcePriority)
    {
        if (GoalSourcePriorityStore.TryGet(goal, out sourcePriority))
        {
            return true;
        }

        if (goal is HaulingBaseGoal haulingGoal)
        {
            var currentPile = haulingGoal.GetTarget(TargetIndex.A).GetObjectAs<ResourcePileInstance>() ??
                              haulingGoal.GetTargetQueue(TargetIndex.A)
                                  .Select(target => target.GetObjectAs<ResourcePileInstance>())
                                  .FirstOrDefault(pile => pile != null);
            if (currentPile != null)
            {
                sourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(currentPile);
                return true;
            }
        }

        if (UnloadCarryContextStore.TryGetSourcePriority(creature, out sourcePriority))
        {
            return true;
        }

        sourcePriority = ZonePriority.None;
        return false;
    }
}
