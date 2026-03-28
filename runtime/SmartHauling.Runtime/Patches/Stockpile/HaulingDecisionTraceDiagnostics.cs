using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using SmartHauling.Runtime.Infrastructure.Reflection;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal static class HaulingDecisionTraceDiagnostics
{
    public static string BuildDecisionContext(StockpileHaulingGoal goal, ResourcePileInstance? firstPile, IStorage? firstStorage)
    {
        if (goal.AgentOwner is not CreatureBase creature || firstPile == null)
        {
            return "decision=<insufficient-context>";
        }

        var allPileInstances = StockpilePileTopology.GetCentralHaulSourcePiles();
        var sameTypeAll = allPileInstances
            .Where(pile => !pile.HasDisposed && pile.Blueprint == firstPile.Blueprint)
            .ToList();

        var sameTypeTargetable = sameTypeAll
            .Where(pile => HaulSourcePolicy.ValidatePile(goal, pile))
            .ToList();
        var allTargetable = allPileInstances
            .Where(pile => !pile.HasDisposed && HaulSourcePolicy.ValidatePile(goal, pile))
            .ToList();

        var agentPosition = creature.GetPosition();
        var sourcePosition = firstPile.GetPosition();
        var targetPosition = PositionReflection.TryGetPosition(firstStorage);
        var agentToSource = Vector3.Distance(agentPosition, sourcePosition);
        var sourceToTarget = targetPosition.HasValue ? Vector3.Distance(sourcePosition, targetPosition.Value) : -1f;
        var agentToTarget = targetPosition.HasValue ? Vector3.Distance(agentPosition, targetPosition.Value) : -1f;
        var routeBudget = StockpileClusterAugmentor.GetDetourBudget(
            sourceToTarget,
            StockpileHaulPolicy.SourceClusterExtent,
            StockpileHaulPolicy.DetourBudgetMultiplier,
            StockpileHaulPolicy.MinimumDetourBudget,
            StockpileHaulPolicy.MaximumDetourBudget);
        var usePatchSweep = StockpileClusterAugmentor.ShouldUsePatchSweep(
            firstPile,
            sameTypeAll,
            StockpileHaulPolicy.PatchSweepAmountThreshold,
            StockpileHaulPolicy.PatchSweepCountThreshold);
        var patchComponent = usePatchSweep
            ? StockpilePileTopology.BuildPatchComponent(firstPile, sameTypeAll, StockpileHaulPolicy.PatchSweepExtent, StockpileHaulPolicy.PatchSweepLinkExtent)
            : new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var sameTypeWithinBudget = sameTypeAll
            .Where(pile => StockpileClusterAugmentor.IsSweepCandidateWorthwhile(
                firstPile,
                pile,
                targetPosition,
                routeBudget,
                usePatchSweep,
                patchComponent,
                StockpileHaulPolicy.SourceClusterExtent,
                out _))
            .ToList();
        var nearbyTargetable = allTargetable
            .Where(pile => Vector3.Distance(sourcePosition, pile.GetPosition()) <= Mathf.Max(StockpileHaulPolicy.PatchSweepExtent, StockpileHaulPolicy.MixedGroundHarvestExtent * 1.5f))
            .ToList();
        var agentStorage = (creature as IStorageAgent)?.Storage;
        var workerFreeSpace = agentStorage?.GetFreeSpace() ?? -1f;
        var workerIgnoreWeight = agentStorage != null && agentStorage.StorageBase.IgnoreWeigth;
        var firstResourceWeight = firstPile.GetStoredResource()?.Weight ?? 0f;

        return
            $"dist[a->s={agentToSource:0.0}, s->t={(sourceToTarget >= 0f ? sourceToTarget.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}, a->t={(agentToTarget >= 0f ? agentToTarget.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}], pos[a={FormatPosition(agentPosition)}, s={FormatPosition(sourcePosition)}, t={FormatPosition(targetPosition)}], worker[free={FormatFloat(workerFreeSpace)}, ignoreWeight={workerIgnoreWeight}, firstWeight={firstResourceWeight:0.##}], sweepMode={(usePatchSweep ? "patch" : "route")}, route[budget={routeBudget:0.0}], sameType[all={sameTypeAll.Count}:{SumAmounts(sameTypeAll)}, targetable={sameTypeTargetable.Count}:{SumAmounts(sameTypeTargetable)}, worthwhile={sameTypeWithinBudget.Count}:{SumAmounts(sameTypeWithinBudget)}], allGroundTargetable={allTargetable.Count}, nearSource=[{SummarizePileGroups(nearbyTargetable)}], available=[{SummarizePileGroups(allTargetable)}]";
    }

    public static string DescribeTaskSeeds(IEnumerable<StockpileTaskSeed> seeds, int maxSeeds = 6)
    {
        if (seeds == null)
        {
            return "<none>";
        }

        var summary = seeds
            .Where(seed => seed?.FirstPile != null)
            .Take(maxSeeds)
            .Select(seed => $"{seed!.FirstPile.BlueprintId}:{seed.EstimatedTotal}/{seed.EstimatedPileCount}p score={seed.Score:0.0}")
            .ToList();

        return summary.Count == 0 ? "<none>" : string.Join("; ", summary);
    }

    private static string SummarizePileGroups(IEnumerable<ResourcePileInstance> piles, int maxGroups = 6)
    {
        var summary = piles
            .Where(pile => pile != null && !pile.HasDisposed)
            .GroupBy(pile => pile.BlueprintId)
            .Select(group => new
            {
                BlueprintId = group.Key,
                Count = group.Count(),
                Amount = group.Sum(pile => pile.GetStoredResource()?.Amount ?? 0),
                Weight = group.Sum(pile =>
                {
                    var resource = pile.GetStoredResource();
                    if (resource == null || resource.HasDisposed)
                    {
                        return 0f;
                    }

                    var unitWeight = Mathf.Max(0.01f, resource.Weight);
                    return resource.Amount * unitWeight;
                })
            })
            .OrderByDescending(entry => entry.Weight)
            .ThenByDescending(entry => entry.Amount)
            .Take(maxGroups)
            .Select(entry => $"{entry.BlueprintId}:{entry.Count}p/{entry.Amount}u/{entry.Weight:0.#}w")
            .ToList();

        return summary.Count == 0 ? "<none>" : string.Join("; ", summary);
    }

    private static string FormatFloat(float value)
    {
        return value < 0f
            ? "n/a"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int SumAmounts(IEnumerable<ResourcePileInstance> piles)
    {
        return piles.Sum(pile => pile.GetStoredResource()?.Amount ?? 0);
    }

    private static string FormatPosition(Vector3? position)
    {
        if (!position.HasValue)
        {
            return "n/a";
        }

        return $"({position.Value.x:0.0},{position.Value.y:0.0},{position.Value.z:0.0})";
    }
}
