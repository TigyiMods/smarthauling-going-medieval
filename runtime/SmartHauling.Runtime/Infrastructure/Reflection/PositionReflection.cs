using HarmonyLib;
using NSMedieval;
using UnityEngine;

namespace SmartHauling.Runtime.Infrastructure.Reflection;

internal static class PositionReflection
{
    public static Vector3? TryGetPosition(object? instance)
    {
        if (instance == null)
        {
            return null;
        }

        var method = AccessTools.Method(instance.GetType(), "GetPosition", System.Type.EmptyTypes);
        if (method == null)
        {
            return null;
        }

        var result = method.Invoke(instance, null);
        return result switch
        {
            Vector3 vector => vector,
            Vec3Int cell => new Vector3(cell.x, cell.y, cell.z),
            _ => null
        };
    }
}
