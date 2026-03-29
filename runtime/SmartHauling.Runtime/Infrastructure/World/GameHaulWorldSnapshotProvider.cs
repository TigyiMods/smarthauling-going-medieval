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
    private const float CentralSourceSnapshotLifetimeSeconds = 0.5f;
    private const float KnownPileSnapshotLifetimeSeconds = 3f;
    private const int StoredPileSweepChunkSize = 192;
    private const int StoredPileColdStartSweepMultiplier = 4;

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

    private readonly object centralSourceSnapshotSyncRoot = new();
    private readonly IncrementalPredicateSetCache<ResourcePileInstance> storedPileCandidateCache =
        new(
            KnownPileSnapshotLifetimeSeconds,
            StoredPileSweepChunkSize,
            StoredPileColdStartSweepMultiplier,
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private IReadOnlyList<ResourcePileInstance> cachedCentralHaulSources = Array.Empty<ResourcePileInstance>();
    private float cachedCentralHaulSourcesExpiresAt;

    public IReadOnlyList<ResourcePileInstance> GetCentralHaulSourcePiles()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        lock (centralSourceSnapshotSyncRoot)
        {
            if (cachedCentralHaulSourcesExpiresAt > now)
            {
                return cachedCentralHaulSources;
            }
        }

        var haulingManager = MonoSingleton<ResourcePileHaulingManager>.Instance;
        var preferredCandidates = new HashSet<ResourcePileInstance>(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
        var hasExplicitReStoreCandidates = false;

        if (haulingManager != null)
        {
            AddPileSequence(preferredCandidates, CanBeStoredProperty.GetValue(haulingManager) as IEnumerable);
            hasExplicitReStoreCandidates = AddPileSequence(preferredCandidates, PilesToReStoreProperty.GetValue(haulingManager) as IEnumerable) > 0;
        }

        var sourceCandidates = hasExplicitReStoreCandidates
            ? preferredCandidates
            : CentralHaulSourceFilter.MergeCandidates(
                preferredCandidates,
                GetStoredPileCandidatesSnapshot(now),
                pile => pile != null,
                ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        var filteredSources = CentralHaulSourceFilter.FilterWithSingleStorageSnapshot(
            sourceCandidates,
            StorageStateSnapshotProvider.GetSnapshot,
            HaulSourcePolicy.CanUseAsCentralHaulSource);
        lock (centralSourceSnapshotSyncRoot)
        {
            cachedCentralHaulSources = filteredSources;
            cachedCentralHaulSourcesExpiresAt = now + CentralSourceSnapshotLifetimeSeconds;
            return cachedCentralHaulSources;
        }
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

    private IReadOnlyList<ResourcePileInstance> GetStoredPileCandidatesSnapshot(float now)
    {
        return storedPileCandidateCache.GetSnapshot(
            now,
            GetAllKnownPileInstances,
            pile => !pile.HasDisposed && pile.PlacedOnStorage != null);
    }

    private static int AddPileSequence(HashSet<ResourcePileInstance> target, IEnumerable? source)
    {
        if (source == null)
        {
            return 0;
        }

        var added = 0;
        foreach (var pile in source.OfType<ResourcePileInstance>())
        {
            if (pile != null && !pile.HasDisposed && target.Add(pile))
            {
                added++;
            }
        }

        return added;
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
