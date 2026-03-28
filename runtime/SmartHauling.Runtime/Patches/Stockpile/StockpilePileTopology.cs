using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime.Patches;

internal static class StockpilePileTopology
{
    public static List<ResourcePileInstance> GetCentralHaulSourcePiles()
    {
        return RuntimeServices.WorldSnapshot.GetCentralHaulSourcePiles().ToList();
    }

    public static float GetNearestPatchDistance(IReadOnlyCollection<ResourcePileInstance> plannedPiles, ResourcePileInstance candidatePile)
    {
        return HaulGeometry.GetNearestPatchDistance(
            plannedPiles.Select(plannedPile => plannedPile.GetPosition()),
            candidatePile.GetPosition());
    }

    public static HashSet<ResourcePileInstance> BuildPatchComponent(
        ResourcePileInstance firstPile,
        IReadOnlyCollection<ResourcePileInstance> sameTypePiles,
        float patchSweepExtent,
        float patchSweepLinkExtent)
    {
        var component = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var frontier = new Queue<ResourcePileInstance>();
        component.Add(firstPile);
        frontier.Enqueue(firstPile);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var candidate in sameTypePiles)
            {
                if (component.Contains(candidate) ||
                    candidate.HasDisposed ||
                    Vector3.Distance(firstPile.GetPosition(), candidate.GetPosition()) > patchSweepExtent ||
                    Vector3.Distance(current.GetPosition(), candidate.GetPosition()) > patchSweepLinkExtent)
                {
                    continue;
                }

                component.Add(candidate);
                frontier.Enqueue(candidate);
            }
        }

        return component;
    }
}
