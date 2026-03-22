using System.Collections.Generic;
using NSMedieval.State;

namespace SmartHauling.Runtime;

internal static class UnloadCarryContextStore
{
    private static readonly Dictionary<CreatureBase, ZonePriority> SourcePriorityByCreature = new();

    public static void SetSourcePriority(CreatureBase creature, ZonePriority sourcePriority)
    {
        if (sourcePriority == ZonePriority.None)
        {
            Clear(creature);
            return;
        }

        SourcePriorityByCreature[creature] = sourcePriority;
    }

    public static bool TryGetSourcePriority(CreatureBase creature, out ZonePriority sourcePriority)
    {
        return SourcePriorityByCreature.TryGetValue(creature, out sourcePriority);
    }

    public static void Clear(CreatureBase creature)
    {
        SourcePriorityByCreature.Remove(creature);
    }
}
