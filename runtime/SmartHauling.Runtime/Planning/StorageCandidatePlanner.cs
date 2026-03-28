using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using NSMedieval.StorageUniversal;
using SmartHauling.Runtime.Infrastructure.Reflection;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class StorageCandidatePlanner
{
    private static readonly PropertyInfo AllStoragesProperty =
        AccessTools.Property(typeof(StorageCommonManager), "AllStorages")!;

    private static readonly PropertyInfo? StockpilesProperty =
        AccessTools.Property(typeof(StockpileManager), "Stockpiles");

    private static readonly FieldInfo? StockpilesField =
        typeof(StockpileManager).GetField("stockpiles", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly ConcurrentDictionary<Type, MethodInfo?> CapacityMethodByType = new();
    private static readonly ConcurrentDictionary<Type, Func<object, Storage?>?> StorageAccessorByType = new();

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

        var preferredPriority = effectiveMinimumPriority;
        requestedAmount = Math.Max(1, requestedAmount);
        var preferredOrderRank = BuildPreferredOrderRank(preferredOrder);
        var candidates = EnumerateStorages()
            .Where(storage => storage != null && !excluded.Contains(storage))
            .Select(storage => CreateCandidate(goal, creature, storage, resourceInstance, sourcePriority, effectiveMinimumPriority, preferredPriority, requestedAmount, preferredOrderRank))
            .Where(candidate => candidate != null)
            .Cast<StorageCandidate>()
            .ToList();

        if (preferredStorage != null && !excluded.Contains(preferredStorage))
        {
            var preferredCandidate = CreateCandidate(goal, creature, preferredStorage, resourceInstance, sourcePriority, effectiveMinimumPriority, preferredPriority, requestedAmount, preferredOrderRank);
            if (preferredCandidate != null && candidates.All(candidate => !ReferenceEquals(candidate.Storage, preferredStorage)))
            {
                candidates.Add(preferredCandidate);
            }
        }

        if (candidates.Count == 0 && enablePriorityFallback && sourcePriority == ZonePriority.None && minimumPriority != ZonePriority.None)
        {
            candidates = EnumerateStorages()
                .Where(storage => storage != null && !excluded.Contains(storage))
                .Select(storage => CreateCandidate(goal, creature, storage, resourceInstance, sourcePriority, ZonePriority.None, ZonePriority.None, requestedAmount, preferredOrderRank))
                .Where(candidate => candidate != null)
                .Cast<StorageCandidate>()
                .ToList();
        }

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.PreferredOrderRank)
            .ThenByDescending(candidate => candidate.FitRatio >= 0.999f)
            .ThenByDescending(candidate => candidate.EstimatedCapacity)
            .ThenBy(candidate => candidate.PriorityOvershoot)
            .ThenBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => preferredStorage != null && ReferenceEquals(candidate.Storage, preferredStorage))
            .ToList();

        return new StorageCandidatePlan(
            orderedCandidates,
            sourcePriority,
            effectiveMinimumPriority,
            requestedAmount);
    }

    private static IEnumerable<IStorage> EnumerateStorages()
    {
        var storages = new List<IStorage>();
        var seen = new HashSet<IStorage>(ReferenceEqualityComparer<IStorage>.Instance);

        AppendStorages(storages, seen, MonoSingleton<StorageCommonManager>.Instance, AllStoragesProperty);
        AppendStorages(storages, seen, MonoSingleton<StockpileManager>.Instance, StockpilesProperty, StockpilesField);

        return storages;
    }

    internal static IReadOnlyList<IStorage> GetAllStoragesSnapshot()
    {
        return EnumerateStorages()
            .Where(storage => storage != null && !storage.HasDisposed)
            .Distinct(ReferenceEqualityComparer<IStorage>.Instance)
            .ToList();
    }

    private static StorageCandidate? CreateCandidate(
        Goal? goal,
        CreatureBase creature,
        IStorage storage,
        ResourceInstance resourceInstance,
        ZonePriority sourcePriority,
        ZonePriority effectiveMinimumPriority,
        ZonePriority preferredPriority,
        int requestedAmount,
        IReadOnlyDictionary<IStorage, int> preferredOrderRank)
    {
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

        var estimatedCapacity = EstimateCapacity(goal, storage, resourceInstance, requestedAmount, out var leasedAmount);
        if (estimatedCapacity <= 0)
        {
            return null;
        }

        var targetPosition = TryGetPosition(storage);
        var distance = targetPosition.HasValue
            ? Vector3.Distance(creature.GetPosition(), targetPosition.Value)
            : float.MaxValue / 4f;
        var fitRatio = Mathf.Clamp01((float)Math.Min(estimatedCapacity, requestedAmount) / requestedAmount);
        var priorityOvershoot = preferredPriority == ZonePriority.None || storage.Priority <= preferredPriority
            ? 0
            : (int)storage.Priority - (int)preferredPriority;
        var preferredRank = preferredOrderRank.TryGetValue(storage, out var rank) ? rank : int.MaxValue;

        return new StorageCandidate(
            storage,
            estimatedCapacity,
            distance,
            fitRatio,
            priorityOvershoot,
            preferredRank,
            targetPosition,
            leasedAmount);
    }

    private static int EstimateCapacity(Goal? goal, IStorage storage, ResourceInstance resourceInstance, int requestedAmount, out int leasedAmount)
    {
        leasedAmount = 0;
        if (resourceInstance?.Blueprint == null)
        {
            return 0;
        }

        var directCapacity = TryInvokeCapacity(storage, resourceInstance.Blueprint);
        if (directCapacity.HasValue)
        {
            leasedAmount = DestinationLeaseStore.GetLeasedAmount(storage, goal);
            return Math.Max(0, directCapacity.Value - leasedAmount);
        }

        var storageComponent = TryResolveStorageComponent(storage);
        if (storageComponent != null)
        {
            var componentCapacity = TryInvokeCapacity(storageComponent, resourceInstance.Blueprint);
            if (componentCapacity.HasValue)
            {
                leasedAmount = DestinationLeaseStore.GetLeasedAmount(storage, goal);
                return Math.Max(0, componentCapacity.Value - leasedAmount);
            }

            var projectedCapacity = PickupPlanningUtil.GetProjectedCapacity(storageComponent, resourceInstance.Blueprint, 0f, false);
            if (projectedCapacity > 0)
            {
                leasedAmount = DestinationLeaseStore.GetLeasedAmount(storage, goal);
                return Math.Max(0, projectedCapacity - leasedAmount);
            }
        }

        leasedAmount = DestinationLeaseStore.GetLeasedAmount(storage, goal);
        return Math.Max(0, Math.Max(1, requestedAmount) - leasedAmount);
    }

    private static int? TryInvokeCapacity(object instance, Resource blueprint)
    {
        var directMethod = CapacityMethodByType.GetOrAdd(instance.GetType(), FindCapacityMethod);
        if (directMethod != null && directMethod.Invoke(instance, new object[] { blueprint }) is int directCapacity)
        {
            return Math.Max(0, directCapacity);
        }

        return null;
    }

    private static MethodInfo? FindCapacityMethod(Type type)
    {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "GetMaximumStorableCount", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Resource) && method.ReturnType == typeof(int);
            });
    }

    private static Storage? TryResolveStorageComponent(object instance)
    {
        if (instance is Storage storageComponent)
        {
            return storageComponent;
        }

        var accessor = StorageAccessorByType.GetOrAdd(instance.GetType(), BuildStorageAccessor);
        return accessor?.Invoke(instance);
    }

    private static Func<object, Storage?>? BuildStorageAccessor(Type type)
    {
        var members = new List<MemberInfo>();
        members.AddRange(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.CanRead && typeof(Storage).IsAssignableFrom(property.PropertyType)));
        members.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => typeof(Storage).IsAssignableFrom(field.FieldType)));
        members.AddRange(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetParameters().Length == 0 && typeof(Storage).IsAssignableFrom(method.ReturnType)));

        var selected = members
            .OrderByDescending(member => member.Name.IndexOf("storage", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
        if (selected == null)
        {
            return null;
        }

        var instanceParameter = Expression.Parameter(typeof(object), "instance");
        var castInstance = Expression.Convert(instanceParameter, type);
        Expression body = selected switch
        {
            PropertyInfo property => Expression.Property(castInstance, property),
            FieldInfo field => Expression.Field(castInstance, field),
            MethodInfo method => Expression.Call(castInstance, method),
            _ => throw new InvalidOperationException()
        };

        var castResult = Expression.TypeAs(body, typeof(Storage));
        return Expression.Lambda<Func<object, Storage?>>(castResult, instanceParameter).Compile();
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

    private static void AppendStorages(
        List<IStorage> target,
        HashSet<IStorage> seen,
        object? manager,
        PropertyInfo? property = null,
        FieldInfo? field = null)
    {
        if (manager == null)
        {
            return;
        }

        var source = property?.GetValue(manager) ?? field?.GetValue(manager);
        if (source is not IEnumerable enumerable)
        {
            return;
        }

        foreach (var item in enumerable)
        {
            if (item is not IStorage storage || !seen.Add(storage))
            {
                continue;
            }

            target.Add(storage);
        }
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
            int priorityOvershoot,
            int preferredOrderRank,
            Vector3? position,
            int leasedAmount)
        {
            Storage = storage;
            EstimatedCapacity = estimatedCapacity;
            Distance = distance;
            FitRatio = fitRatio;
            PriorityOvershoot = priorityOvershoot;
            PreferredOrderRank = preferredOrderRank;
            Position = position;
            LeasedAmount = leasedAmount;
        }

        public IStorage Storage { get; }

        public int EstimatedCapacity { get; }

        public float Distance { get; }

        public float FitRatio { get; }

        public int PriorityOvershoot { get; }

        public int PreferredOrderRank { get; }

        public Vector3? Position { get; }

        public int LeasedAmount { get; }
    }
}
