using NSMedieval.State;

namespace SmartHauling.Runtime.Infrastructure.World;

/// <summary>
/// Provides read-only snapshots of haul-relevant world state for planning and coordination.
/// </summary>
internal interface IHaulWorldSnapshotProvider
{
    /// <summary>
    /// Gets the central set of piles that should currently be considered as hauling sources.
    /// </summary>
    IReadOnlyList<ResourcePileInstance> GetCentralHaulSourcePiles();

    /// <summary>
    /// Gets all known pile instances that are still alive in the world.
    /// </summary>
    IReadOnlyList<ResourcePileInstance> GetAllKnownPileInstances();

    /// <summary>
    /// Gets creatures that can be considered by centralized hauling assignment.
    /// </summary>
    IEnumerable<CreatureBase> GetCreatures();
}
