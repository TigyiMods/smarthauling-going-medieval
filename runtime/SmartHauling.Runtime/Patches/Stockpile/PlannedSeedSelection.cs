using NSMedieval;
using NSMedieval.State;

namespace SmartHauling.Runtime.Patches;

internal sealed class PlannedSeedSelection
{
    public PlannedSeedSelection(
        ResourcePileInstance firstPile,
        IReadOnlyList<ResourcePileInstance> sourcePatchPiles,
        IStorage primaryStorage,
        ZonePriority sourcePriority,
        StorageCandidatePlanner.StorageCandidatePlan candidatePlan,
        int requestedAmount,
        int destinationBudget,
        int pickupBudget,
        int estimatedTotal,
        int estimatedResourceTypes,
        int estimatedPileCount,
        float score)
    {
        FirstPile = firstPile;
        SourcePatchPiles = sourcePatchPiles;
        PrimaryStorage = primaryStorage;
        SourcePriority = sourcePriority;
        CandidatePlan = candidatePlan;
        RequestedAmount = requestedAmount;
        DestinationBudget = destinationBudget;
        PickupBudget = pickupBudget;
        EstimatedTotal = estimatedTotal;
        EstimatedResourceTypes = estimatedResourceTypes;
        EstimatedPileCount = estimatedPileCount;
        Score = score;
    }

    public ResourcePileInstance FirstPile { get; }

    public IReadOnlyList<ResourcePileInstance> SourcePatchPiles { get; }

    public IStorage PrimaryStorage { get; }

    public ZonePriority SourcePriority { get; }

    public StorageCandidatePlanner.StorageCandidatePlan CandidatePlan { get; }

    public int RequestedAmount { get; }

    public int DestinationBudget { get; }

    public int PickupBudget { get; }

    public int EstimatedTotal { get; }

    public int EstimatedResourceTypes { get; }

    public int EstimatedPileCount { get; }

    public float Score { get; }
}
