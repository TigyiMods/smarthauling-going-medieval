using NSMedieval.Goap;

namespace SmartHauling.Runtime.Infrastructure.Reservations;

/// <summary>
/// Narrow abstraction over the game's reservation manager.
/// </summary>
/// <remarks>
/// The interface intentionally works with <see cref="object"/> to keep the boundary resilient to
/// game-side interface visibility and signature changes.
/// </remarks>
internal interface IReservationService
{
    /// <summary>
    /// Attempts to reserve a reservable world object for the specified GOAP owner.
    /// </summary>
    bool TryReserveObject(object reservable, IGoapAgentOwner owner);

    /// <summary>
    /// Releases all reservations associated with the provided world object.
    /// </summary>
    void ReleaseAll(object reservable);

    /// <summary>
    /// Releases a specific reservation held by the given owner.
    /// </summary>
    void ReleaseObject(object reservable, IGoapAgentOwner owner);
}
