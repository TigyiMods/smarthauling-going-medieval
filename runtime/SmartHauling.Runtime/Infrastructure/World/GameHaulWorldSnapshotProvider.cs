using System.Collections;
using System.Reflection;
using HarmonyLib;
using NSEipix.Base;
using NSMedieval.Manager;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;

namespace SmartHauling.Runtime.Infrastructure.World;

internal sealed class GameHaulWorldSnapshotProvider : IHaulWorldSnapshotProvider
{
    private const float StoredPileSnapshotLifetimeSeconds = 1f;

    private static readonly PropertyInfo? CreaturesProperty =
        AccessTools.Property(typeof(CreatureManager), "Creatures");

    private static readonly FieldInfo? CreaturesField =
        AccessTools.Field(typeof(CreatureManager), "creatures");

    private static readonly PropertyInfo AllPileInstancesProperty =
        AccessTools.Property(typeof(ResourcePileManager), "AllPileInstances")!;

    private static readonly PropertyInfo CanBeStoredProperty =
        AccessTools.Property(typeof(ResourcePileHaulingManager), "CanBeStored")!;

    private static readonly PropertyInfo PilesToReStoreProperty =
        AccessTools.Property(typeof(ResourcePileHaulingManager), "PilesToReStore")!;

    private readonly object storedPileSnapshotSyncRoot = new();
    private IReadOnlyList<ResourcePileInstance> cachedStoredPileCandidates = Array.Empty<ResourcePileInstance>();
    private float cachedStoredPileCandidatesExpiresAt;

    public IReadOnlyList<ResourcePileInstance> GetCentralHaulSourcePiles()
    {
        var haulingManager = MonoSingleton<ResourcePileHaulingManager>.Instance;
        var preferredCandidates = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        if (haulingManager != null)
        {
            AddPileSequence(preferredCandidates, CanBeStoredProperty.GetValue(haulingManager) as IEnumerable);
            AddPileSequence(preferredCandidates, PilesToReStoreProperty.GetValue(haulingManager) as IEnumerable);
        }

        var mergedCandidates = CentralHaulSourceFilter.MergeCandidates(
            preferredCandidates,
            GetStoredPileCandidatesSnapshot(),
            pile => pile != null,
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        return CentralHaulSourceFilter.FilterWithSingleStorageSnapshot(
            mergedCandidates,
            StorageCandidatePlanner.GetAllStoragesSnapshot,
            HaulSourcePolicy.CanUseAsCentralHaulSource);
    }

    public IReadOnlyList<ResourcePileInstance> GetAllKnownPileInstances()
    {
        var pileManager = MonoSingleton<ResourcePileManager>.Instance;
        if (pileManager == null)
        {
            return new List<ResourcePileInstance>();
        }

        var allPileInstances = AllPileInstancesProperty.GetValue(pileManager) as IEnumerable;
        if (allPileInstances == null)
        {
            return new List<ResourcePileInstance>();
        }

        return allPileInstances
            .OfType<ResourcePileInstance>()
            .Where(pile => pile != null && !pile.HasDisposed)
            .ToList();
    }

    public IEnumerable<CreatureBase> GetCreatures()
    {
        var manager = MonoSingleton<CreatureManager>.Instance;
        if (manager == null)
        {
            yield break;
        }

        foreach (var creature in GetCreatureEnumerable(manager).OfType<CreatureBase>())
        {
            if (creature != null && !creature.HasDisposed)
            {
                yield return creature;
            }
        }
    }

    private IReadOnlyList<ResourcePileInstance> GetStoredPileCandidatesSnapshot()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        lock (storedPileSnapshotSyncRoot)
        {
            if (cachedStoredPileCandidatesExpiresAt > now)
            {
                return cachedStoredPileCandidates;
            }

            cachedStoredPileCandidates = GetAllKnownPileInstances()
                .Where(pile => pile != null && !pile.HasDisposed && pile.PlacedOnStorage != null)
                .ToList();
            cachedStoredPileCandidatesExpiresAt = now + StoredPileSnapshotLifetimeSeconds;
            return cachedStoredPileCandidates;
        }
    }

    private static void AddPileSequence(HashSet<ResourcePileInstance> target, IEnumerable? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var pile in source.OfType<ResourcePileInstance>())
        {
            if (pile != null && !pile.HasDisposed)
            {
                target.Add(pile);
            }
        }
    }

    private static IEnumerable<object> GetCreatureEnumerable(CreatureManager manager)
    {
        if (CreaturesProperty?.GetValue(manager) is IEnumerable creaturesByProperty)
        {
            foreach (var creature in creaturesByProperty)
            {
                yield return creature;
            }

            yield break;
        }

        if (CreaturesField?.GetValue(manager) is IEnumerable creaturesByField)
        {
            foreach (var creature in creaturesByField)
            {
                yield return creature;
            }
        }
    }
}
