using System.Collections;
using System.Reflection;
using HarmonyLib;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using NSMedieval.StorageUniversal;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Infrastructure.Reflection;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class StorageStateSnapshotProvider
{
    private const float SnapshotLifetimeSeconds = 0.25f;

    private static readonly PropertyInfo AllStoragesProperty =
        AccessTools.Property(typeof(StorageCommonManager), "AllStorages")!;

    private static readonly PropertyInfo? StockpilesProperty =
        AccessTools.Property(typeof(StockpileManager), "Stockpiles");

    private static readonly FieldInfo? StockpilesField =
        typeof(StockpileManager).GetField("stockpiles", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly object SyncRoot = new();
    private static IReadOnlyList<StorageStateSnapshotEntry> cachedSnapshot = Array.Empty<StorageStateSnapshotEntry>();
    private static float cachedSnapshotExpiresAt;

    public static IReadOnlyList<StorageStateSnapshotEntry> GetSnapshot()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        lock (SyncRoot)
        {
            if (cachedSnapshotExpiresAt > now)
            {
                return cachedSnapshot;
            }

            var leasedAmountsByStorage = DestinationLeaseStore.GetLeasedAmountSnapshot();
            var storages = new List<StorageStateSnapshotEntry>();
            var seen = new HashSet<IStorage>(ReferenceEqualityComparer<IStorage>.Instance);
            AppendStorages(storages, seen, leasedAmountsByStorage, MonoSingleton<StorageCommonManager>.Instance, AllStoragesProperty);
            AppendStorages(storages, seen, leasedAmountsByStorage, MonoSingleton<StockpileManager>.Instance, StockpilesProperty, StockpilesField);

            cachedSnapshot = storages;
            cachedSnapshotExpiresAt = now + SnapshotLifetimeSeconds;
            return cachedSnapshot;
        }
    }

    public static StorageStateSnapshotEntry CreateDetachedState(IStorage storage)
    {
        return new StorageStateSnapshotEntry(
            storage,
            storage?.Priority ?? ZonePriority.None,
            PositionReflection.TryGetPosition(storage),
            DestinationLeaseStore.GetLeasedAmount(storage));
    }

    private static void AppendStorages(
        List<StorageStateSnapshotEntry> target,
        HashSet<IStorage> seen,
        IReadOnlyDictionary<IStorage, int> leasedAmountsByStorage,
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

            target.Add(new StorageStateSnapshotEntry(
                storage,
                storage.Priority,
                PositionReflection.TryGetPosition(storage),
                leasedAmountsByStorage.TryGetValue(storage, out var leasedAmount) ? leasedAmount : 0));
        }
    }
}

internal sealed class StorageStateSnapshotEntry
{
    public StorageStateSnapshotEntry(IStorage storage, ZonePriority priority, Vector3? position, int leasedAmount)
    {
        Storage = storage;
        Priority = priority;
        Position = position;
        LeasedAmount = leasedAmount;
    }

    public IStorage Storage { get; }

    public ZonePriority Priority { get; }

    public Vector3? Position { get; }

    public int LeasedAmount { get; }

    public bool IsRoutable =>
        Storage != null &&
        !Storage.HasDisposed &&
        !Storage.Underwater &&
        !Storage.IsOnFire;
}
