using NSMedieval.State;

namespace SmartHauling.Runtime;

/// <summary>
/// Worker-independent task seed describing a candidate source patch before claim-time materialization.
/// </summary>
/// <remarks>
/// The board stores and leases these seeds centrally. A claimed seed is later expanded into a full
/// coordinated task with concrete pickup and destination plans for a specific worker.
/// </remarks>
internal sealed class StockpileTaskSeed
{
    public StockpileTaskSeed(
        ResourcePileInstance firstPile,
        IReadOnlyList<ResourcePileInstance> sourcePatchPiles,
        ZonePriority sourcePriority,
        int estimatedTotal,
        int estimatedResourceTypes,
        int estimatedPileCount,
        bool usesPatchSweep,
        float score)
    {
        FirstPile = firstPile;
        SourcePatchPiles = sourcePatchPiles;
        SourcePriority = sourcePriority;
        EstimatedTotal = estimatedTotal;
        EstimatedResourceTypes = estimatedResourceTypes;
        EstimatedPileCount = estimatedPileCount;
        UsesPatchSweep = usesPatchSweep;
        Score = score;
    }

    public ResourcePileInstance FirstPile { get; }

    public IReadOnlyList<ResourcePileInstance> SourcePatchPiles { get; }

    public ZonePriority SourcePriority { get; }

    public int EstimatedTotal { get; }

    public int EstimatedResourceTypes { get; }

    public int EstimatedPileCount { get; }

    public bool UsesPatchSweep { get; }

    public float Score { get; }
}
