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
        IReadOnlyCollection<IStorage> destinationStorages,
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
        var destinationAnchors = BuildDestinationAnchors(firstStorage, destinationStorages);
        var targetPosition = GetNearestTargetPosition(firstPile.GetPosition(), destinationAnchors);
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

            if (storedResource == null || storedResource.HasDisposed)
            {
                continue;
            }

            var pileSourcePriority = StoragePriorityUtil.GetEffectiveSourcePriority(pile);
            if (!TrySelectCompatibleDestination(
                    creature,
                    pile,
                    storedResource,
                    pileSourcePriority,
                    destinationAnchors,
                    out var compatibleTargetPosition,
                    out var compatibilityRejection))
            {
                if (compatibilityRejection == "priority")
                {
                    priorityRejected++;
                    CaptureDetail(detailSamples, $"{pile.BlueprintId}:priority({pileSourcePriority}->{firstStorage.Priority})");
                }
                else
                {
                    storageRejected++;
                    CaptureDetail(detailSamples, $"{pile.BlueprintId}:store");
                }

                continue;
            }

            if (!StockpileClusterAugmentor.IsSweepCandidateWorthwhile(
                    firstPile,
                    pile,
                    compatibleTargetPosition,
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
                        : compatibleTargetPosition.HasValue
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

            candidatePiles.Add(pile);
        }

        var orderedCandidates = PickupRouteOrdering.OrderCandidates(
            candidatePiles,
            firstPile.GetPosition(),
            targetPosition);

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

    private static IReadOnlyList<DestinationAnchor> BuildDestinationAnchors(
        IStorage firstStorage,
        IReadOnlyCollection<IStorage> destinationStorages)
    {
        var anchors = new List<DestinationAnchor>();
        var seen = new HashSet<IStorage>(ReferenceEqualityComparer<IStorage>.Instance);
        AddAnchor(firstStorage, anchors, seen);
        foreach (var storage in destinationStorages.Where(storage => storage != null))
        {
            AddAnchor(storage, anchors, seen);
        }

        return anchors;
    }

    private static void AddAnchor(
        IStorage storage,
        ICollection<DestinationAnchor> anchors,
        ISet<IStorage> seen)
    {
        if (storage == null || storage.HasDisposed || !seen.Add(storage))
        {
            return;
        }

        anchors.Add(new DestinationAnchor(storage, StockpileClusterAugmentor.TryGetPosition(storage)));
    }

    private static Vector3? GetNearestTargetPosition(
        Vector3 sourcePosition,
        IReadOnlyList<DestinationAnchor> destinationAnchors)
    {
        Vector3? bestPosition = null;
        var bestDistance = float.MaxValue;
        foreach (var anchor in destinationAnchors)
        {
            if (!anchor.Position.HasValue)
            {
                continue;
            }

            var distance = Vector3.Distance(sourcePosition, anchor.Position.Value);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestPosition = anchor.Position;
        }

        return bestPosition;
    }

    private static bool TrySelectCompatibleDestination(
        CreatureBase creature,
        ResourcePileInstance pile,
        ResourceInstance storedResource,
        ZonePriority sourcePriority,
        IReadOnlyList<DestinationAnchor> destinationAnchors,
        out Vector3? compatibleTargetPosition,
        out string rejection)
    {
        compatibleTargetPosition = null;
        rejection = "priority";

        var priorityMatched = false;
        var foundCompatible = false;
        var bestDistance = float.MaxValue;
        foreach (var anchor in destinationAnchors)
        {
            var storage = anchor.Storage;
            if (storage == null || storage.HasDisposed)
            {
                continue;
            }

            if (!HaulingPriorityRules.CanMoveToPriority(sourcePriority, storage.Priority))
            {
                continue;
            }

            priorityMatched = true;
            if (!storage.CanStore(storedResource, creature))
            {
                continue;
            }

            foundCompatible = true;
            if (!anchor.Position.HasValue)
            {
                continue;
            }

            var distance = anchor.Position.HasValue
                ? Vector3.Distance(pile.GetPosition(), anchor.Position.Value)
                : float.MaxValue / 4f;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            compatibleTargetPosition = anchor.Position;
        }

        if (compatibleTargetPosition.HasValue || foundCompatible)
        {
            rejection = "ok";
            return true;
        }

        rejection = priorityMatched ? "store" : "priority";
        return false;
    }

    private sealed class DestinationAnchor
    {
        public DestinationAnchor(IStorage storage, Vector3? position)
        {
            Storage = storage;
            Position = position;
        }

        public IStorage Storage { get; }

        public Vector3? Position { get; }
    }

    private static void CaptureDetail(List<string> details, string value)
    {
        if (details.Count < 8)
        {
            details.Add(value);
        }
    }
}
