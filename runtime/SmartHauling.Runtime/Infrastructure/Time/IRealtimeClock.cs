namespace SmartHauling.Runtime.Infrastructure.Time;

/// <summary>
/// Provides the current monotonic realtime value used by leases, cooldowns, and runtime coordination.
/// </summary>
internal interface IRealtimeClock
{
    /// <summary>
    /// Gets the current realtime in seconds since process start.
    /// </summary>
    float RealtimeSinceStartup { get; }
}
