using SmartHauling.Runtime.Infrastructure.Reservations;
using SmartHauling.Runtime.Infrastructure.Time;
using SmartHauling.Runtime.Infrastructure.World;

namespace SmartHauling.Runtime.Composition;

/// <summary>
/// Central composition root for lightweight runtime services used by the hauling orchestration flow.
/// </summary>
/// <remarks>
/// Patch adapters and runtime components resolve engine-facing boundaries from here instead of
/// calling Unity or game singletons directly.
/// </remarks>
internal static class RuntimeServices
{
    static RuntimeServices()
    {
        InitializeDefaults();
    }

    /// <summary>
    /// Provides monotonic realtime values for leases, cooldowns, and trace timestamps.
    /// </summary>
    public static IRealtimeClock Clock { get; private set; } = null!;

    /// <summary>
    /// Wraps game reservation APIs behind a narrow runtime boundary.
    /// </summary>
    public static IReservationService Reservations { get; private set; } = null!;

    /// <summary>
    /// Provides stable read access to haul-related world snapshots.
    /// </summary>
    public static IHaulWorldSnapshotProvider WorldSnapshot { get; private set; } = null!;

    /// <summary>
    /// Recreates the default runtime adapters for normal in-game execution.
    /// </summary>
    public static void InitializeDefaults()
    {
        Configure(
            new UnityRealtimeClock(),
            new GameReservationService(),
            new GameHaulWorldSnapshotProvider());
    }

    /// <summary>
    /// Replaces the active runtime service set. Intended for tests and controlled startup wiring.
    /// </summary>
    internal static void Configure(
        IRealtimeClock clock,
        IReservationService reservations,
        IHaulWorldSnapshotProvider worldSnapshot)
    {
        Clock = clock ?? throw new System.ArgumentNullException(nameof(clock));
        Reservations = reservations ?? throw new System.ArgumentNullException(nameof(reservations));
        WorldSnapshot = worldSnapshot ?? throw new System.ArgumentNullException(nameof(worldSnapshot));
    }
}
