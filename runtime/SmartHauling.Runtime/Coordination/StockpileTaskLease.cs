using NSMedieval.Goap;
using NSMedieval.State;

namespace SmartHauling.Runtime;

internal sealed class StockpileTaskLease
{
    public StockpileTaskLease(
        Goal goal,
        CreatureBase owner,
        ResourcePileInstance firstPile,
        IEnumerable<ResourcePileInstance> sourcePatchPiles,
        float expiresAt)
    {
        Goal = goal;
        Owner = owner;
        FirstPile = firstPile;
        SourcePatchPiles = sourcePatchPiles
            .Where(pile => pile != null)
            .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
            .ToArray();
        ExpiresAt = expiresAt;
    }

    public Goal Goal { get; }

    public CreatureBase Owner { get; }

    public ResourcePileInstance FirstPile { get; }

    public IReadOnlyList<ResourcePileInstance> SourcePatchPiles { get; }

    public float ExpiresAt { get; set; }
}
