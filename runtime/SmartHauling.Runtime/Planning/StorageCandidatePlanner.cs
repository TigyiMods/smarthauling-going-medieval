using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.State;
using SmartHauling.Runtime.Infrastructure.Reflection;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class StorageCandidatePlanner
{
    public static StorageCandidatePlan BuildPlan(
        Goal? goal,
        CreatureBase creature,
        ResourceInstance resourceInstance,
        ZonePriority minimumPriority,
        ZonePriority sourcePriority,
        bool enablePriorityFallback,
        int requestedAmount,
        IStorage? preferredStorage = null,
        IEnumerable<IStorage>? preferredOrder = null,
        IEnumerable<IStorage>? exclude = null)
    {
        if (creature == null || resourceInstance == null || resourceInstance.HasDisposed)
        {
            return StorageCandidatePlan.Empty(sourcePriority, minimumPriority);
        }

        var effectiveMinimumPriority = sourcePriority == ZonePriority.None
            ? minimumPriority
            : HaulingPriorityRules.GetRequiredMinimumPriority(sourcePriority, minimumPriority);

        var excluded = exclude != null
            ? new HashSet<IStorage>(exclude, ReferenceEqualityComparer<IStorage>.Instance)
            : new HashSet<IStorage>(ReferenceEqualityComparer<IStorage>.Instance);

        requestedAmount = Math.Max(1, requestedAmount);
        var preferredOrderRank = BuildPreferredOrderRank(preferredOrder);
        var storageStates = StorageStateSnapshotProvider.GetSnapshot();
        var candidates = storageStates
            .Where(storageState => storageState.Storage != null && !excluded.Contains(storageState.Storage))
            .Select(storageState => CreateCandidate(goal, creature, storageState, resourceInstance, sourcePriority, effectiveMinimumPriority, requestedAmount, preferredOrderRank))
            .Where(candidate => candidate != null)
            .Cast<StorageCandidate>()
            .ToList();

        if (preferredStorage != null && !excluded.Contains(preferredStorage))
        {
            var preferredState = storageStates.FirstOrDefault(storageState => ReferenceEquals(storageState.Storage, preferredStorage)) ??
                                 StorageStateSnapshotProvider.CreateDetachedState(preferredStorage);
            var preferredCandidate = CreateCandidate(goal, creature, preferredState, resourceInstance, sourcePriority, effectiveMinimumPriority, requestedAmount, preferredOrderRank);
            if (preferredCandidate != null && candidates.All(candidate => !ReferenceEquals(candidate.Storage, preferredStorage)))
            {
                candidates.Add(preferredCandidate);
            }
        }

        if (candidates.Count == 0 && enablePriorityFallback && sourcePriority == ZonePriority.None && minimumPriority != ZonePriority.None)
        {
            candidates = storageStates
                .Where(storageState => storageState.Storage != null && !excluded.Contains(storageState.Storage))
                .Select(storageState => CreateCandidate(goal, creature, storageState, resourceInstance, sourcePriority, ZonePriority.None, requestedAmount, preferredOrderRank))
                .Where(candidate => candidate != null)
                .Cast<StorageCandidate>()
                .ToList();
        }

        var orderedCandidates = StorageCandidateOrdering.OrderCandidates(candidates, preferredStorage);

        return new StorageCandidatePlan(
            orderedCandidates,
            sourcePriority,
            effectiveMinimumPriority,
            requestedAmount);
    }

    internal static IReadOnlyList<IStorage> GetAllStoragesSnapshot()
    {
        return StorageStateSnapshotProvider.GetSnapshot()
            .Select(storageState => storageState.Storage)
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
    }

    private static StorageCandidate? CreateCandidate(
        Goal? goal,
        CreatureBase creature,
        StorageStateSnapshotEntry storageState,
        ResourceInstance resourceInstance,
        ZonePriority sourcePriority,
        ZonePriority effectiveMinimumPriority,
        int requestedAmount,
        IReadOnlyDictionary<IStorage, int> preferredOrderRank)
    {
        if (storageState == null)
        {
            return null;
        }

        var storage = storageState.Storage;
        if (storage == null ||
            storage.HasDisposed ||
            storage.Underwater ||
            storage.IsOnFire ||
            !storage.CanStore(resourceInstance, creature))
        {
            return null;
        }

        if (sourcePriority != ZonePriority.None && !HaulingPriorityRules.CanMoveToPriority(sourcePriority, storage.Priority))
        {
            return null;
        }

        if (effectiveMinimumPriority != ZonePriority.None && storage.Priority < effectiveMinimumPriority)
        {
            return null;
        }

        var estimatedCapacity = StorageCapacityEstimator.EstimateCapacity(goal, storageState, resourceInstance, requestedAmount, out var leasedAmount);
        if (estimatedCapacity <= 0)
        {
            return null;
        }

        var distance = storageState.Position.HasValue
            ? Vector3.Distance(creature.GetPosition(), storageState.Position.Value)
            : float.MaxValue / 4f;
        var fitRatio = Mathf.Clamp01((float)Math.Min(estimatedCapacity, requestedAmount) / requestedAmount);
        var preferredRank = preferredOrderRank.TryGetValue(storage, out var rank) ? rank : int.MaxValue;

        return new StorageCandidate(
            storage,
            estimatedCapacity,
            distance,
            fitRatio,
            preferredRank,
            storageState.Position,
            leasedAmount);
    }

    private static IReadOnlyDictionary<IStorage, int> BuildPreferredOrderRank(IEnumerable<IStorage>? preferredOrder)
    {
        if (preferredOrder == null)
        {
            return new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);
        }

        var result = new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);
        var index = 0;
        foreach (var storage in preferredOrder.Where(storage => storage != null))
        {
            if (result.ContainsKey(storage))
            {
                continue;
            }

            result[storage] = index++;
        }

        return result;
    }

    internal static Vector3? TryGetPosition(object? instance)
    {
        return PositionReflection.TryGetPosition(instance);
    }

    internal sealed class StorageCandidatePlan
    {
        internal StorageCandidatePlan(
            IReadOnlyList<StorageCandidate> candidates,
            ZonePriority sourcePriority,
            ZonePriority effectiveMinimumPriority,
            int requestedAmount)
        {
            Candidates = candidates;
            SourcePriority = sourcePriority;
            EffectiveMinimumPriority = effectiveMinimumPriority;
            RequestedAmount = requestedAmount;
        }

        public IReadOnlyList<StorageCandidate> Candidates { get; }

        public ZonePriority SourcePriority { get; }

        public ZonePriority EffectiveMinimumPriority { get; }

        public int RequestedAmount { get; }

        public StorageCandidate? Primary => Candidates.FirstOrDefault();

        public IReadOnlyList<IStorage> OrderedStorages =>
            Candidates.Select(candidate => candidate.Storage).Distinct(ReferenceEqualityComparer<IStorage>.Instance).ToList();

        public int GetEstimatedCapacityBudget(int limit)
        {
            var requested = Math.Max(1, Math.Min(RequestedAmount, limit));
            var total = 0;
            foreach (var candidate in Candidates)
            {
                total += candidate.EstimatedCapacity;
                if (total >= requested)
                {
                    return requested;
                }
            }

            return Math.Min(limit, total);
        }

        public string Summarize(int maxCandidates = 4)
        {
            if (Candidates.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                "; ",
                Candidates.Take(maxCandidates).Select(candidate =>
                    $"{candidate.Storage.GetType().Name}[prio={candidate.Storage.Priority}, cap={candidate.EstimatedCapacity}, lease={candidate.LeasedAmount}, fit={candidate.FitRatio:0.00}, dist={(candidate.Distance >= float.MaxValue / 8f ? "n/a" : candidate.Distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}]"));
        }

        public static StorageCandidatePlan Empty(ZonePriority sourcePriority, ZonePriority effectiveMinimumPriority)
        {
            return new StorageCandidatePlan(Array.Empty<StorageCandidate>(), sourcePriority, effectiveMinimumPriority, 0);
        }
    }

    internal sealed class StorageCandidate
    {
        public StorageCandidate(
            IStorage storage,
            int estimatedCapacity,
            float distance,
            float fitRatio,
            int preferredOrderRank,
            Vector3? position,
            int leasedAmount)
        {
            Storage = storage;
            EstimatedCapacity = estimatedCapacity;
            Distance = distance;
            FitRatio = fitRatio;
            PreferredOrderRank = preferredOrderRank;
            Position = position;
            LeasedAmount = leasedAmount;
        }

        public IStorage Storage { get; }

        public int EstimatedCapacity { get; }

        public float Distance { get; }

        public float FitRatio { get; }

        public int PreferredOrderRank { get; }

        public Vector3? Position { get; }

        public int LeasedAmount { get; }
    }
}
