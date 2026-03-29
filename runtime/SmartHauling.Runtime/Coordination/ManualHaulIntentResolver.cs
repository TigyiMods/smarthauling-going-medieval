using NSEipix.Base;
using NSMedieval;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class ManualHaulIntentResolver
{
    public static PlayerForcedHaulIntentStore.PendingIntent? ResolveEffectiveIntent(
        StockpileHaulingGoal goal,
        CreatureBase? creature,
        PlayerForcedHaulIntentStore.PendingIntent? explicitIntent,
        ResourcePileInstance? observedFirstPile)
    {
        if (explicitIntent.HasValue || goal is not StockpileUrgentHaulingGoal)
        {
            return explicitIntent;
        }

        return BuildUrgentPriorityIntent(goal, creature, observedFirstPile);
    }

    public static string DescribeIntent(
        PlayerForcedHaulIntentStore.PendingIntent? intent,
        ResourcePileInstance? firstPile)
    {
        if (!intent.HasValue)
        {
            return "<none>";
        }

        var anchorToSource = firstPile != null
            ? Vector3.Distance(intent.Value.AnchorPosition, firstPile.GetPosition())
            : -1f;
        var samePile = intent.Value.ContainsPriorityPile(firstPile);
        return $"anchor={intent.Value.AnchorBlueprintId}, pending={intent.Value.PriorityPiles.Count}, match={samePile}, anchor->source={(anchorToSource >= 0f ? anchorToSource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}";
    }

    public static IStorage? ResolvePreferredStorageForAnchor(
        CreatureBase? creature,
        IStorage? preferredStorage,
        ResourcePileInstance? anchorPile)
    {
        if (creature == null || preferredStorage == null || anchorPile == null)
        {
            return null;
        }

        var storedResource = anchorPile.GetStoredResource();
        return storedResource != null && preferredStorage.CanStore(storedResource, creature)
            ? preferredStorage
            : null;
    }

    private static PlayerForcedHaulIntentStore.PendingIntent? BuildUrgentPriorityIntent(
        StockpileHaulingGoal goal,
        CreatureBase? creature,
        ResourcePileInstance? observedFirstPile)
    {
        if (creature == null)
        {
            return null;
        }

        var urgentCandidates = StockpileTaskBoard.GetPendingUrgentPilesSnapshot();
        var anchorPile = PlayerForcedPriorityPlanner.SelectPreferredAnchor(
            observedFirstPile,
            urgentCandidates.OrderBy(pile => Vector3.Distance(creature.GetPosition(), pile.GetPosition())),
            pile => IsEligibleUrgentAnchor(goal, creature, pile),
            pile => Vector3.Distance(creature.GetPosition(), pile.GetPosition()),
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        if (anchorPile == null)
        {
            DiagnosticTrace.Info(
                "haul.urgent",
                $"No eligible urgent anchor for {goal.AgentOwner}: vanillaFirst={DescribePileSummary(observedFirstPile)}",
                120);
            return null;
        }

        var anchorPosition = anchorPile.GetPosition();
        var priorityPiles = PlayerForcedPriorityPlanner.SelectLocalPrioritySeeds(
            anchorPile,
            urgentCandidates
                .OrderBy(pile => Vector3.Distance(anchorPosition, pile.GetPosition())),
            StockpileHaulPolicy.PlayerForcedSourceClusterExtent,
            IsUrgentPriorityPile,
            pile => Vector3.Distance(anchorPosition, pile.GetPosition()),
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        if (!ReferenceEquals(anchorPile, observedFirstPile))
        {
            DiagnosticTrace.Info(
                "haul.urgent",
                $"Resolved urgent anchor override for {goal.AgentOwner}: vanillaFirst={DescribePileSummary(observedFirstPile)}, selected={DescribePileSummary(anchorPile)}, pending={priorityPiles.Count}",
                120);
        }

        var blueprintId = anchorPile.BlueprintId ?? anchorPile.GetStoredResource()?.BlueprintId ?? "<unknown>";
        return new PlayerForcedHaulIntentStore.PendingIntent(
            anchorPile,
            blueprintId,
            anchorPosition,
            RuntimeServices.Clock.RealtimeSinceStartup,
            priorityPiles);
    }

    private static bool IsUrgentPriorityPile(ResourcePileInstance? pile)
    {
        return pile != null &&
               !pile.HasDisposed &&
               pile.IsUrgentHaul &&
               pile.OwnedByPlayer();
    }

    private static bool IsEligibleUrgentAnchor(
        StockpileHaulingGoal goal,
        CreatureBase creature,
        ResourcePileInstance? pile)
    {
        if (pile == null ||
            !IsUrgentPriorityPile(pile) ||
            HaulFailureBackoffStore.IsCoolingDown(pile) ||
            !ClusterOwnershipStore.CanUsePile(creature, pile) ||
            !HaulSourcePolicy.CanReachPile(goal, pile) ||
            !HaulSourcePolicy.ValidatePile(goal, pile))
        {
            return false;
        }

        var reservations = MonoSingleton<ReservationManager>.Instance;
        return reservations == null ||
               reservations.IsReservedBy(pile, goal.AgentOwner) ||
               reservations.CanReserve(pile, goal.AgentOwner);
    }

    private static string DescribePileSummary(ResourcePileInstance? pile)
    {
        if (pile == null)
        {
            return "<none>";
        }

        var blueprintId = pile.BlueprintId ?? pile.GetStoredResource()?.BlueprintId ?? "<unknown>";
        var amount = pile.GetStoredResource()?.Amount ?? 0;
        return $"{blueprintId}:{amount}, urgent={pile.IsUrgentHaul}";
    }
}
