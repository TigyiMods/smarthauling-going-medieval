using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NSEipix.Base;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal sealed class CoordinatedDropReservation
{
    public CoordinatedDropReservation(string resourceId, Resource blueprint, int reservedAmount, IStorage storage, Vec3Int position)
    {
        ResourceId = resourceId;
        Blueprint = blueprint;
        ReservedAmount = reservedAmount;
        Storage = storage;
        Position = position;
    }

    public string ResourceId { get; }

    public Resource Blueprint { get; }

    public int ReservedAmount { get; }

    public IStorage Storage { get; }

    public Vec3Int Position { get; }
}

internal sealed class CoordinatedStockpileExecutionState
{
    public CoordinatedDropReservation? ActiveDrop { get; set; }

    public int ConsecutiveDropFailures { get; set; }

    public HashSet<string> FailedDropKeys { get; } = new();

    public bool DropPhaseLocked { get; set; }

    public Vector3? LastPickupPosition { get; set; }
}

internal static class CoordinatedStockpileExecutionStore
{
    private static readonly ConditionalWeakTable<Goal, CoordinatedStockpileExecutionState> States = new();

    public static CoordinatedStockpileExecutionState GetOrCreate(Goal goal)
    {
        return States.GetOrCreateValue(goal);
    }

    public static bool TryGet(Goal goal, out CoordinatedStockpileExecutionState state)
    {
        if (goal != null && States.TryGetValue(goal, out state!))
        {
            return true;
        }

        state = null!;
        return false;
    }

    public static void SetActiveDrop(Goal goal, CoordinatedDropReservation reservation)
    {
        if (goal == null || reservation == null)
        {
            return;
        }

        GetOrCreate(goal).ActiveDrop = reservation;
    }

    public static int IncrementDropFailures(Goal goal)
    {
        if (goal == null)
        {
            return 0;
        }

        var state = GetOrCreate(goal);
        state.ConsecutiveDropFailures++;
        return state.ConsecutiveDropFailures;
    }

    public static void ResetDropFailures(Goal goal)
    {
        if (goal == null || !TryGet(goal, out var state))
        {
            return;
        }

        state.ConsecutiveDropFailures = 0;
    }

    public static bool IsDropPhaseLocked(Goal goal)
    {
        return goal != null &&
               TryGet(goal, out var state) &&
               state.DropPhaseLocked;
    }

    public static void MarkDropPhaseStarted(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        GetOrCreate(goal).DropPhaseLocked = true;
    }

    public static void ResetDropPhase(Goal goal)
    {
        if (goal == null || !TryGet(goal, out var state))
        {
            return;
        }

        state.DropPhaseLocked = false;
    }

    public static void RememberPickupPosition(Goal goal, Vector3 position)
    {
        if (goal == null)
        {
            return;
        }

        GetOrCreate(goal).LastPickupPosition = position;
    }

    public static bool TryGetLastPickupPosition(Goal goal, out Vector3 position)
    {
        if (goal != null &&
            TryGet(goal, out var state) &&
            state.LastPickupPosition.HasValue)
        {
            position = state.LastPickupPosition.Value;
            return true;
        }

        position = default;
        return false;
    }

    public static bool HasFailedDrop(Goal goal, string resourceId, IStorage storage, Vec3Int position)
    {
        return goal != null &&
               TryGet(goal, out var state) &&
               state.FailedDropKeys.Contains(BuildDropKey(resourceId, storage, position));
    }

    public static void MarkFailedDrop(Goal goal, CoordinatedDropReservation reservation)
    {
        if (goal == null || reservation == null)
        {
            return;
        }

        GetOrCreate(goal).FailedDropKeys.Add(BuildDropKey(reservation.ResourceId, reservation.Storage, reservation.Position));
    }

    public static void ClearActiveDrop(Goal goal, CreatureBase? owner = null)
    {
        if (!TryGet(goal, out var state))
        {
            return;
        }

        ReleaseReservation(state.ActiveDrop, owner);
        state.ActiveDrop = null;
    }

    public static void Clear(Goal goal, CreatureBase? owner = null)
    {
        if (goal == null)
        {
            return;
        }

        if (States.TryGetValue(goal, out var state))
        {
            ReleaseReservation(state.ActiveDrop, owner);
        }

        States.Remove(goal);
    }

    private static void ReleaseReservation(CoordinatedDropReservation? reservation, CreatureBase? owner)
    {
        if (reservation?.Storage == null || reservation.Storage.HasDisposed || owner == null || owner.HasDisposed)
        {
            return;
        }

        reservation.Storage.ReleaseReservations(owner);
    }

    private static string BuildDropKey(string resourceId, IStorage storage, Vec3Int position)
    {
        return resourceId + "|" + RuntimeHelpers.GetHashCode(storage) + "|" + position.x + "|" + position.y + "|" + position.z;
    }
}
