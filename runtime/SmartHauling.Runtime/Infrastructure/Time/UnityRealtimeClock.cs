namespace SmartHauling.Runtime.Infrastructure.Time;

internal sealed class UnityRealtimeClock : IRealtimeClock
{
    public float RealtimeSinceStartup => UnityEngine.Time.realtimeSinceStartup;
}
