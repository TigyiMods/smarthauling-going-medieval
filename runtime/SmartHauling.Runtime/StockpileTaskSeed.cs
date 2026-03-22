using System.Collections.Generic;
using NSMedieval.Model;
using NSMedieval.State;

namespace SmartHauling.Runtime;

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
