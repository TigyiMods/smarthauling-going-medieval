using System.Collections.Generic;
using System.Linq;
using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.State;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class DestinationLeaseStore
{
    private const float LeaseLifetimeSeconds = 20f;

    private static readonly object SyncRoot = new();
    private static readonly List<DestinationLease> ActiveLeases = new();

    public static int GetLeasedAmount(IStorage? storage, Goal? excludingGoal = null)
    {
        if (storage == null)
        {
            return 0;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            return ActiveLeases
                .Where(lease =>
                    lease.Storage != null &&
                    ReferenceEquals(lease.Storage, storage) &&
                    lease.Amount > 0 &&
                    (excludingGoal == null || !ReferenceEquals(lease.Goal, excludingGoal)))
                .Sum(lease => lease.Amount);
        }
    }

    public static int LeasePlan(
        Goal goal,
        CreatureBase owner,
        IReadOnlyList<StorageCandidatePlanner.StorageCandidate> candidates,
        int requestedAmount)
    {
        if (goal == null || owner == null || candidates == null || candidates.Count == 0 || requestedAmount <= 0)
        {
            return 0;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            ReleaseGoalUnsafe(goal);

            var remaining = requestedAmount;
            var expiresAt = Time.realtimeSinceStartup + LeaseLifetimeSeconds;
            var leased = 0;
            foreach (var candidate in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (candidate.Storage == null ||
                    candidate.Storage.HasDisposed ||
                    candidate.EstimatedCapacity <= 0)
                {
                    continue;
                }

                var amount = Mathf.Min(candidate.EstimatedCapacity, remaining);
                if (amount <= 0)
                {
                    continue;
                }

                ActiveLeases.Add(new DestinationLease(goal, owner, candidate.Storage, amount, expiresAt));
                remaining -= amount;
                leased += amount;
            }

            return leased;
        }
    }

    public static int LeasePlans(
        Goal goal,
        CreatureBase owner,
        IEnumerable<StorageCandidatePlanner.StorageCandidatePlan> candidatePlans)
    {
        if (goal == null || owner == null || candidatePlans == null)
        {
            return 0;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            ReleaseGoalUnsafe(goal);

            var expiresAt = Time.realtimeSinceStartup + LeaseLifetimeSeconds;
            var leased = 0;
            foreach (var candidatePlan in candidatePlans)
            {
                if (candidatePlan == null || candidatePlan.Candidates.Count == 0 || candidatePlan.RequestedAmount <= 0)
                {
                    continue;
                }

                var remaining = candidatePlan.RequestedAmount;
                foreach (var candidate in candidatePlan.Candidates)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    if (candidate.Storage == null ||
                        candidate.Storage.HasDisposed ||
                        candidate.EstimatedCapacity <= 0)
                    {
                        continue;
                    }

                    var amount = Mathf.Min(candidate.EstimatedCapacity, remaining);
                    if (amount <= 0)
                    {
                        continue;
                    }

                    ActiveLeases.Add(new DestinationLease(goal, owner, candidate.Storage, amount, expiresAt));
                    remaining -= amount;
                    leased += amount;
                }
            }

            return leased;
        }
    }

    public static void RefreshGoal(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            var expiresAt = Time.realtimeSinceStartup + LeaseLifetimeSeconds;
            foreach (var lease in ActiveLeases.Where(lease => ReferenceEquals(lease.Goal, goal)))
            {
                lease.ExpiresAt = expiresAt;
            }
        }
    }

    public static void ReleaseGoal(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            ReleaseGoalUnsafe(goal);
        }
    }

    private static void CleanupExpiredLeases()
    {
        var now = Time.realtimeSinceStartup;
        ActiveLeases.RemoveAll(lease =>
            lease.Storage == null ||
            lease.Storage.HasDisposed ||
            lease.Owner == null ||
            lease.Owner.HasDisposed ||
            lease.Goal == null ||
            lease.Amount <= 0 ||
            lease.ExpiresAt <= now);
    }

    private static void ReleaseGoalUnsafe(Goal goal)
    {
        ActiveLeases.RemoveAll(lease => ReferenceEquals(lease.Goal, goal));
    }

    private sealed class DestinationLease
    {
        public DestinationLease(Goal goal, CreatureBase owner, IStorage storage, int amount, float expiresAt)
        {
            Goal = goal;
            Owner = owner;
            Storage = storage;
            Amount = amount;
            ExpiresAt = expiresAt;
        }

        public Goal Goal { get; }

        public CreatureBase Owner { get; }

        public IStorage Storage { get; }

        public int Amount { get; }

        public float ExpiresAt { get; set; }
    }
}
