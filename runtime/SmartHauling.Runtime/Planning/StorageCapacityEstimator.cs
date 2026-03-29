using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using NSMedieval.BuildingComponents;
using NSMedieval;
using NSMedieval.Components;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using NSMedieval.StorageUniversal;

namespace SmartHauling.Runtime;

internal static class StorageCapacityEstimator
{
    private const int ConservativeFallbackCapacity = 1;

    private static readonly ConcurrentDictionary<Type, MethodInfo?> CapacityMethodByType = new();
    private static readonly ConcurrentDictionary<Type, Func<object, Storage?>?> StorageAccessorByType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> StockpileCanStoreMethodByType = new();

    public static int EstimateCapacity(
        Goal? goal,
        StorageStateSnapshotEntry storageState,
        ResourceInstance resourceInstance,
        int requestedAmount,
        out int leasedAmount)
    {
        leasedAmount = 0;
        if (storageState.Storage == null || resourceInstance?.Blueprint == null)
        {
            return 0;
        }

        leasedAmount = GetEffectiveLeasedAmount(storageState, goal);

        var topologyCapacity = TryEstimateTopologyCapacity(
            storageState.Storage,
            resourceInstance,
            requestedAmount,
            leasedAmount);
        if (topologyCapacity.HasValue)
        {
            return topologyCapacity.Value;
        }

        var directCapacity = TryInvokeCapacity(storageState.Storage, resourceInstance.Blueprint);
        int? componentCapacity = null;
        int? projectedCapacity = null;
        var storageComponent = TryResolveStorageComponent(storageState.Storage);
        if (storageComponent != null)
        {
            componentCapacity = TryInvokeCapacity(storageComponent, resourceInstance.Blueprint);
            projectedCapacity = PickupPlanningUtil.GetProjectedCapacity(storageComponent, resourceInstance.Blueprint, 0f, false);
        }

        return ResolveAvailableCapacity(leasedAmount, directCapacity, componentCapacity, projectedCapacity);
    }

    internal static int ResolveAvailableCapacity(int leasedAmount, params int?[] estimates)
    {
        var availableEstimates = estimates
            .Where(estimate => estimate.HasValue)
            .Select(estimate => Math.Max(0, estimate!.Value))
            .ToList();
        if (availableEstimates.Count > 0)
        {
            // Prefer the most conservative remaining-capacity signal so near-full storages do not
            // advertise their full theoretical maximum and soak up reprioritization tasks.
            return Math.Max(0, availableEstimates.Min() - Math.Max(0, leasedAmount));
        }

        return Math.Max(0, ConservativeFallbackCapacity - Math.Max(0, leasedAmount));
    }

    private static int GetEffectiveLeasedAmount(StorageStateSnapshotEntry storageState, Goal? goal)
    {
        if (goal == null)
        {
            return storageState.LeasedAmount;
        }

        var goalLeasedAmount = DestinationLeaseStore.GetLeasedAmountForGoal(storageState.Storage, goal);
        return Math.Max(0, storageState.LeasedAmount - goalLeasedAmount);
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

    private static int? TryEstimateTopologyCapacity(
        IStorage storage,
        ResourceInstance resourceInstance,
        int requestedAmount,
        int leasedAmount)
    {
        if (storage == null || resourceInstance?.Blueprint == null)
        {
            return null;
        }

        var target = Math.Max(1, requestedAmount + Math.Max(0, leasedAmount));
        int? estimated = storage switch
        {
            StockpileInstance stockpile => EstimateStockpileCapacity(stockpile, resourceInstance, target),
            ShelfComponentInstance shelf => EstimateShelfCapacity(shelf, resourceInstance, target),
            _ => null
        };

        if (!estimated.HasValue)
        {
            return null;
        }

        return Math.Max(0, estimated.Value - Math.Max(0, leasedAmount));
    }

    private static int EstimateStockpileCapacity(
        StockpileInstance stockpile,
        ResourceInstance resourceInstance,
        int targetCapacity)
    {
        if (stockpile == null ||
            resourceInstance?.Blueprint == null ||
            stockpile.HasDisposed ||
            stockpile.Grid == null ||
            stockpile.Grid.Count == 0)
        {
            return 0;
        }

        var stockpileCanStoreAtCell = StockpileCanStoreMethodByType.GetOrAdd(stockpile.GetType(), FindStockpileCanStoreMethod);
        var stackingLimit = Math.Max(1, resourceInstance.Blueprint.StackingLimit);
        var total = 0;

        foreach (var space in stockpile.Grid.Values)
        {
            if (space == null)
            {
                continue;
            }

            if (stockpileCanStoreAtCell != null)
            {
                if (stockpileCanStoreAtCell.Invoke(stockpile, new object[] { resourceInstance, space.Position, true }) is not bool canStoreAtCell ||
                    !canStoreAtCell)
                {
                    continue;
                }
            }

            if (HasConflictingReservations(space, resourceInstance.Blueprint))
            {
                continue;
            }

            var reservedAmount = GetReservedAmount(space, resourceInstance.Blueprint);
            var pile = space.Pile;
            var storedResource = pile?.GetStoredResource();
            if (storedResource != null &&
                !storedResource.HasDisposed &&
                storedResource.Blueprint != resourceInstance.Blueprint)
            {
                continue;
            }

            var slotCapacity = storedResource != null && !storedResource.HasDisposed
                ? Math.Max(1, storedResource.StackingLimit) - storedResource.Amount
                : stackingLimit;
            total += Math.Max(0, slotCapacity - reservedAmount);
            if (total >= targetCapacity)
            {
                return targetCapacity;
            }
        }

        return Math.Max(0, total);
    }

    private static int EstimateShelfCapacity(
        ShelfComponentInstance shelf,
        ResourceInstance resourceInstance,
        int targetCapacity)
    {
        if (shelf == null ||
            resourceInstance?.Blueprint == null ||
            shelf.HasDisposed ||
            shelf.AllStorage == null ||
            shelf.AllStorage.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var universalStorage in shelf.AllStorage)
        {
            if (universalStorage == null || universalStorage.HasDisposed)
            {
                continue;
            }

            if (!universalStorage.CanStore(resourceInstance))
            {
                continue;
            }

            var free = Math.Max(0, universalStorage.GetFreeSpace(resourceInstance.Blueprint));
            if (free <= 0)
            {
                continue;
            }

            var reserved = GetReservedAmount(universalStorage, resourceInstance.Blueprint);
            total += Math.Max(0, free - reserved);
            if (total >= targetCapacity)
            {
                return targetCapacity;
            }
        }

        return Math.Max(0, total);
    }

    private static int GetReservedAmount(StockpileSpaceData space, Resource blueprint)
    {
        if (space?.ReservationInfos == null || blueprint == null)
        {
            return 0;
        }

        return space.ReservationInfos
            .Where(info => info.Blueprint == blueprint)
            .Sum(info => Math.Max(0, info.Amount));
    }

    private static bool HasConflictingReservations(StockpileSpaceData space, Resource blueprint)
    {
        if (space?.ReservationInfos == null || blueprint == null)
        {
            return false;
        }

        return space.ReservationInfos.Any(info => info.Blueprint != null && info.Blueprint != blueprint);
    }

    private static int GetReservedAmount(UniversalStorage universalStorage, Resource blueprint)
    {
        if (universalStorage?.StorageSlots == null || blueprint == null)
        {
            return 0;
        }

        var reserved = 0;
        foreach (var slot in universalStorage.StorageSlots)
        {
            if (slot == null || !slot.HasReservation())
            {
                continue;
            }

            if (slot.ReservationInfo.Blueprint == blueprint)
            {
                reserved += Math.Max(0, slot.ReservationInfo.Amount);
            }
        }

        return reserved;
    }

    private static MethodInfo? FindStockpileCanStoreMethod(Type type)
    {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "CanStore", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                       parameters[0].ParameterType == typeof(ResourceInstance) &&
                       parameters[1].ParameterType == typeof(Vec3Int) &&
                       parameters[2].ParameterType == typeof(bool) &&
                       method.ReturnType == typeof(bool);
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
}
