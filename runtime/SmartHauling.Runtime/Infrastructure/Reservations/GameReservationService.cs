using System.Reflection;
using NSEipix.Base;
using NSMedieval.Goap;
using NSMedieval.Manager;

namespace SmartHauling.Runtime.Infrastructure.Reservations;

internal sealed class GameReservationService : IReservationService
{
    private static readonly MethodInfo? TryReserveObjectMethod = typeof(ReservationManager)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(method => method.Name == "TryReserveObject" && method.GetParameters().Length == 2);

    private static readonly MethodInfo? ReleaseAllMethod = typeof(ReservationManager)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(method => method.Name == "ReleaseAll" && method.GetParameters().Length == 1);

    private static readonly MethodInfo? ReleaseObjectMethod = typeof(ReservationManager)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(method => method.Name == "ReleaseObject" && method.GetParameters().Length == 2);

    public bool TryReserveObject(object reservable, IGoapAgentOwner owner)
    {
        var manager = MonoSingleton<ReservationManager>.Instance;
        if (manager == null || reservable == null || owner == null || TryReserveObjectMethod == null)
        {
            return false;
        }

        return TryReserveObjectMethod.Invoke(manager, new[] { reservable, owner }) is bool reserved && reserved;
    }

    public void ReleaseAll(object reservable)
    {
        var manager = MonoSingleton<ReservationManager>.Instance;
        if (manager == null || reservable == null || ReleaseAllMethod == null)
        {
            return;
        }

        ReleaseAllMethod.Invoke(manager, new[] { reservable });
    }

    public void ReleaseObject(object reservable, IGoapAgentOwner owner)
    {
        var manager = MonoSingleton<ReservationManager>.Instance;
        if (manager == null || reservable == null || owner == null || ReleaseObjectMethod == null)
        {
            return;
        }

        ReleaseObjectMethod.Invoke(manager, new[] { reservable, owner });
    }
}
