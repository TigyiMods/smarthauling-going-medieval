using NSMedieval;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal sealed class StockpileSameTypeSweepResult
{
    public StockpileSameTypeSweepResult(
        IReadOnlyList<ResourcePileInstance> orderedCandidates,
        bool usePatchSweep,
        float sourceToTargetDistance,
        float detourBudget,
        int sameTypeTotal,
        int sameTypeAmount,
        int sameTypeWithinBudget,
        int sameTypeWithinBudgetAmount,
        int claimedByOther,
        int validateRejected,
        int priorityRejected,
        int storageRejected,
        int detourRejected,
        int reachRejected,
        int cooldownRejected,
        IReadOnlyList<string> detailSamples)
    {
        OrderedCandidates = orderedCandidates;
        UsePatchSweep = usePatchSweep;
        SourceToTargetDistance = sourceToTargetDistance;
        DetourBudget = detourBudget;
        SameTypeTotal = sameTypeTotal;
        SameTypeAmount = sameTypeAmount;
        SameTypeWithinBudget = sameTypeWithinBudget;
        SameTypeWithinBudgetAmount = sameTypeWithinBudgetAmount;
        ClaimedByOther = claimedByOther;
        ValidateRejected = validateRejected;
        PriorityRejected = priorityRejected;
        StorageRejected = storageRejected;
        DetourRejected = detourRejected;
        ReachRejected = reachRejected;
        CooldownRejected = cooldownRejected;
        DetailSamples = detailSamples;
    }

    public IReadOnlyList<ResourcePileInstance> OrderedCandidates { get; }

    public bool UsePatchSweep { get; }

    public float SourceToTargetDistance { get; }

    public float DetourBudget { get; }

    public int SameTypeTotal { get; }

    public int SameTypeAmount { get; }

    public int SameTypeWithinBudget { get; }

    public int SameTypeWithinBudgetAmount { get; }

    public int ClaimedByOther { get; }

    public int ValidateRejected { get; }

    public int PriorityRejected { get; }

    public int StorageRejected { get; }

    public int DetourRejected { get; }

    public int ReachRejected { get; }

    public int CooldownRejected { get; }

    public IReadOnlyList<string> DetailSamples { get; }
}

internal static class StockpileSameTypeSweepPlanner
{
    public static StockpileSameTypeSweepResult Build(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sourcePatchPiles,
        IStorage firstStorage,
        float sourceClusterExtent,
        float patchSweepExtent,
        float patchSweepLinkExtent,
        int patchSweepAmountThreshold,
        int patchSweepCountThreshold,
        float minimumDetourBudget,
        float maximumDetourBudget,
        float detourBudgetMultiplier)
    {
        var candidatePiles = new List<ResourcePileInstance>();
        var sameTypeTotal = 0;
        var sameTypeAmount = 0;
        var sameTypeWithinBudget = 0;
        var sameTypeWithinBudgetAmount = 0;
        var claimedByOther = 0;
        var validateRejected = 0;
        var priorityRejected = 0;
        var storageRejected = 0;
        var detourRejected = 0;
        var reachRejected = 0;
        var cooldownRejected = 0;
        var detailSamples = new List<string>();
        var targetPosition = StockpileClusterAugmentor.TryGetPosition(firstStorage);
        var sourceToTargetDistance = targetPosition.HasValue
            ? Vector3.Distance(firstPile.GetPosition(), targetPosition.Value)
            : -1f;
        var detourBudget = StockpileClusterAugmentor.GetDetourBudget(
            sourceToTargetDistance,
            sourceClusterExtent,
            detourBudgetMultiplier,
            minimumDetourBudget,
            maximumDetourBudget);
        var sameTypePiles = sourcePatchPiles
            .Where(pile => pile != null && !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();
        var usePatchSweep = StockpileClusterAugmentor.ShouldUsePatchSweep(
            firstPile,
            sameTypePiles,
            patchSweepAmountThreshold,
            patchSweepCountThreshold);
        var patchComponent = usePatchSweep
            ? StockpilePileTopology.BuildPatchComponent(firstPile, sameTypePiles, patchSweepExtent, patchSweepLinkExtent)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        foreach (var pile in sameTypePiles)
        {
            if (pile.HasDisposed || pile.Blueprint != firstPile.Blueprint)
            {
                continue;
            }

            var storedResource = pile.GetStoredResource();
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeTotal++;
                sameTypeAmount += storedResource.Amount;
            }

            if (!StockpileClusterAugmentor.IsSweepCandidateWorthwhile(
                    firstPile,
                    pile,
                    targetPosition,
                    detourBudget,
                    usePatchSweep,
                    patchComponent,
                    sourceClusterExtent,
                    out var detourCost))
            {
                detourRejected++;
                CaptureDetail(
                    detailSamples,
                    usePatchSweep
                        ? $"{pile.BlueprintId}:patch({detourCost:0.0}>{patchSweepExtent:0.0})"
                        : targetPosition.HasValue
                            ? $"{pile.BlueprintId}:detour({detourCost:0.0}>{detourBudget:0.0})"
                            : $"{pile.BlueprintId}:radius");
                continue;
            }

            sameTypeWithinBudget++;
            if (storedResource != null && !storedResource.HasDisposed)
            {
                sameTypeWithinBudgetAmount += storedResource.Amount;
            }

            if (!ClusterOwnershipStore.CanUsePile(creature, pile))
            {
                claimedByOther++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:claimed");
                continue;
            }

            if (HaulFailureBackoffStore.IsCoolingDown(pile))
            {
                cooldownRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:cooldown");
                continue;
            }

            if (!HaulSourcePolicy.CanReachPile(goal, pile))
            {
                reachRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:reach");
                continue;
            }

            if (!HaulSourcePolicy.ValidatePile(goal, pile))
            {
                validateRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:validate");
                continue;
            }

            if (storedResource == null || storedResource.HasDisposed)
            {
                continue;
            }

            var pileSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
            if (!HaulingPriorityRules.CanMoveToPriority(pileSourcePriority, firstStorage.Priority))
            {
                priorityRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:priority({pileSourcePriority}->{firstStorage.Priority})");
                continue;
            }

            if (!firstStorage.CanStore(storedResource, creature))
            {
                storageRejected++;
                CaptureDetail(detailSamples, $"{pile.BlueprintId}:store");
                continue;
            }

            candidatePiles.Add(pile);
        }

        var orderedCandidates = candidatePiles
            .OrderBy(pile => usePatchSweep
                ? Vector3.Distance(firstPile.GetPosition(), pile.GetPosition())
                : StockpileClusterAugmentor.GetAdditionalDetour(firstPile, pile, targetPosition))
            .ThenBy(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()))
            .ToList();

        return new StockpileSameTypeSweepResult(
            orderedCandidates,
            usePatchSweep,
            sourceToTargetDistance,
            detourBudget,
            sameTypeTotal,
            sameTypeAmount,
            sameTypeWithinBudget,
            sameTypeWithinBudgetAmount,
            claimedByOther,
            validateRejected,
            priorityRejected,
            storageRejected,
            detourRejected,
            reachRejected,
            cooldownRejected,
            detailSamples);
    }

    private static void CaptureDetail(List<string> details, string value)
    {
        if (details.Count < 8)
        {
            details.Add(value);
        }
    }
}
