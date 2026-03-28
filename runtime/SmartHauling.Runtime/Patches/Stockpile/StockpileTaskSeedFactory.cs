using NSEipix;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Types;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal static class StockpileTaskSeedFactory
{
    public static StockpileTaskSeed? TryCreate(
        ResourcePileInstance firstPile,
        IReadOnlyList<ResourcePileInstance> allPiles,
        float sourceClusterExtent,
        float mixedGroundHarvestExtent,
        int patchSweepAmountThreshold,
        int patchSweepCountThreshold,
        float patchSweepExtent,
        float patchSweepLinkExtent,
        float minimumSourceSliceWeightBudget,
        float nominalWorkerFreeSpace)
    {
        if (firstPile == null ||
            firstPile.HasDisposed ||
            HaulFailureBackoffStore.IsCoolingDown(firstPile))
        {
            return null;
        }

        var storedResource = firstPile.GetStoredResource();
        if (storedResource == null || storedResource.HasDisposed)
        {
            return null;
        }

        var sameTypePiles = allPiles
            .Where(pile => !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();
        if (sameTypePiles.Count == 0)
        {
            return null;
        }

        var usePatchSweep = HaulGeometry.ShouldUsePatchSweep(
            firstPile.BlueprintId,
            storedResource.Amount,
            sameTypePiles.Count,
            patchSweepAmountThreshold,
            patchSweepCountThreshold);
        var fullSourcePatchPiles = usePatchSweep
            ? StockpilePileTopology.BuildPatchComponent(firstPile, sameTypePiles, patchSweepExtent, patchSweepLinkExtent)
            : new HashSet<ResourcePileInstance>(
                sameTypePiles.Where(pile => Vector3.Distance(firstPile.GetPosition(), pile.GetPosition()) <= sourceClusterExtent),
                ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        fullSourcePatchPiles.Add(firstPile);

        var sliceBudgetWeight = StockpileSourceSlicePlanner.GetNominalSourceSliceWeightBudget(
            nominalWorkerFreeSpace,
            minimumSourceSliceWeightBudget,
            IsSingleHeavyItem(storedResource.Blueprint),
            storedResource.Blueprint?.Weight ?? 0f);
        var sliceCandidates = fullSourcePatchPiles
            .Where(pile => pile != null && !pile.HasDisposed)
            .Select(pile =>
            {
                var resource = pile.GetStoredResource();
                var nominalWeight = resource == null || resource.HasDisposed
                    ? 0f
                    : GetNominalPileWeight(resource);
                return new SourceSliceCandidate<ResourcePileInstance>(
                    pile,
                    pile.GetPosition(),
                    resource?.Amount ?? 0,
                    nominalWeight);
            })
            .ToList();

        var sourcePatchPiles = StockpileSourceSlicePlanner.BuildSlice(
            firstPile,
            firstPile.GetPosition(),
            sliceCandidates,
            sliceBudgetWeight,
            ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToList();
        var sourcePatchSet = new HashSet<ResourcePileInstance>(sourcePatchPiles, ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        var mixedPatchPiles = allPiles
            .Where(pile =>
                !pile.HasDisposed &&
                !sourcePatchSet.Contains(pile) &&
                IsNearSourcePatch(sourcePatchPiles, pile, mixedGroundHarvestExtent))
            .ToList();

        var estimatedTotal = SumAmounts(sourcePatchPiles) + SumAmounts(mixedPatchPiles);
        if (estimatedTotal <= 0)
        {
            return null;
        }

        var estimatedPileCount = sourcePatchPiles.Count + mixedPatchPiles.Count;
        var estimatedResourceTypes = sourcePatchPiles
            .Concat(mixedPatchPiles)
            .Select(pile => pile.BlueprintId)
            .Distinct()
            .Count();
        var patchExtent = HaulGeometry.GetPatchExtent(
            firstPile.GetPosition(),
            sourcePatchPiles.Concat(mixedPatchPiles).Select(pile => pile.GetPosition()));
        var score = HaulingScore.CalculateTaskSeedScore(
            estimatedTotal,
            patchExtent,
            estimatedResourceTypes,
            estimatedPileCount,
            firstPile.BlueprintId.Contains("sapling", System.StringComparison.OrdinalIgnoreCase));

        return new StockpileTaskSeed(
            firstPile,
            sourcePatchPiles,
            StoragePriorityUtil.GetEffectiveSourcePriority(firstPile),
            estimatedTotal,
            estimatedResourceTypes,
            estimatedPileCount,
            usePatchSweep,
            score);
    }

    private static bool IsNearSourcePatch(
        IReadOnlyCollection<ResourcePileInstance> sourcePatchPiles,
        ResourcePileInstance candidatePile,
        float mixedGroundHarvestExtent)
    {
        return StockpilePileTopology.GetNearestPatchDistance(sourcePatchPiles, candidatePile) <= mixedGroundHarvestExtent;
    }

    private static float GetNominalPileWeight(ResourceInstance resource)
    {
        if (resource == null || resource.HasDisposed)
        {
            return 0f;
        }

        var blueprint = resource.Blueprint;
        return StockpileSourceSlicePlanner.GetNominalPileWeight(
            blueprint != null,
            IsSingleHeavyItem(blueprint),
            blueprint?.Weight ?? 0f,
            resource.Amount);
    }

    private static bool IsSingleHeavyItem(Resource? resource)
    {
        if (resource == null)
        {
            return false;
        }

        return resource.EquipmentBlueprint != null ||
               (resource.Category & (ResourceCategory.CtgCarcass | ResourceCategory.CtgStructure)) != ResourceCategory.None ||
               (resource.Category == ResourceCategory.None && resource.GetID().Contains("trophy"));
    }

    private static int SumAmounts(IEnumerable<ResourcePileInstance> piles)
    {
        return piles.Sum(pile => pile.GetStoredResource()?.Amount ?? 0);
    }
}
