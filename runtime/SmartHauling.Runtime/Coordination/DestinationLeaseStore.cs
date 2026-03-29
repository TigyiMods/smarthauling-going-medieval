using NSMedieval;
using NSMedieval.Goap;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class DestinationLeaseStore
{
    private const float LeaseLifetimeSeconds = 20f;

    private static readonly object SyncRoot = new();
    private static readonly List<DestinationLease> ActiveLeases = new();
    private static readonly Dictionary<IStorage, int> ActiveLeasedAmountByStorage =
        new(ReferenceEqualityComparer<IStorage>.Instance);
    private static readonly Dictionary<Goal, Dictionary<IStorage, int>> ActiveLeasedAmountByGoal =
        new(ReferenceEqualityComparer<Goal>.Instance);

    public static int GetLeasedAmount(IStorage? storage, Goal? excludingGoal = null)
    {
        if (storage == null)
        {
            return 0;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            var total = ActiveLeasedAmountByStorage.TryGetValue(storage, out var leasedAmount)
                ? leasedAmount
                : 0;
            if (excludingGoal == null)
            {
                return total;
            }

            return Mathf.Max(0, total - GetGoalLeasedAmountUnsafe(storage, excludingGoal));
        }
    }

    public static int GetLeasedAmountForGoal(IStorage? storage, Goal? goal)
    {
        if (storage == null || goal == null)
        {
            return 0;
        }

        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            return GetGoalLeasedAmountUnsafe(storage, goal);
        }
    }

    public static IReadOnlyDictionary<IStorage, int> GetLeasedAmountSnapshot()
    {
        lock (SyncRoot)
        {
            CleanupExpiredLeases();
            return ActiveLeasedAmountByStorage.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                ReferenceEqualityComparer<IStorage>.Instance);
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
            var expiresAt = RuntimeServices.Clock.RealtimeSinceStartup + LeaseLifetimeSeconds;
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

                TrackLeaseUnsafe(new DestinationLease(goal, owner, candidate.Storage, amount, expiresAt));
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

            var expiresAt = RuntimeServices.Clock.RealtimeSinceStartup + LeaseLifetimeSeconds;
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

                    TrackLeaseUnsafe(new DestinationLease(goal, owner, candidate.Storage, amount, expiresAt));
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
            var expiresAt = RuntimeServices.Clock.RealtimeSinceStartup + LeaseLifetimeSeconds;
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
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var expired = ActiveLeases
            .Where(lease =>
                lease.Storage == null ||
                lease.Storage.HasDisposed ||
                lease.Owner == null ||
                lease.Owner.HasDisposed ||
                lease.Goal == null ||
                lease.Amount <= 0 ||
                lease.ExpiresAt <= now)
            .ToList();
        foreach (var lease in expired)
        {
            UntrackLeaseUnsafe(lease);
        }

        if (expired.Count > 0)
        {
            var expiredSet = new HashSet<DestinationLease>(expired);
            ActiveLeases.RemoveAll(lease => expiredSet.Contains(lease));
        }
    }

    private static void ReleaseGoalUnsafe(Goal goal)
    {
        var released = ActiveLeases
            .Where(lease => ReferenceEquals(lease.Goal, goal))
            .ToList();
        foreach (var lease in released)
        {
            UntrackLeaseUnsafe(lease);
        }

        if (released.Count > 0)
        {
            var releasedSet = new HashSet<DestinationLease>(released);
            ActiveLeases.RemoveAll(lease => releasedSet.Contains(lease));
        }
    }

    private static void TrackLeaseUnsafe(DestinationLease lease)
    {
        ActiveLeases.Add(lease);
        AddLeasedAmountUnsafe(ActiveLeasedAmountByStorage, lease.Storage, lease.Amount);
        if (!ActiveLeasedAmountByGoal.TryGetValue(lease.Goal, out var goalStorageAmounts))
        {
            goalStorageAmounts = new Dictionary<IStorage, int>(ReferenceEqualityComparer<IStorage>.Instance);
            ActiveLeasedAmountByGoal[lease.Goal] = goalStorageAmounts;
        }

        AddLeasedAmountUnsafe(goalStorageAmounts, lease.Storage, lease.Amount);
    }

    private static void UntrackLeaseUnsafe(DestinationLease lease)
    {
        SubtractLeasedAmountUnsafe(ActiveLeasedAmountByStorage, lease.Storage, lease.Amount);
        if (!ActiveLeasedAmountByGoal.TryGetValue(lease.Goal, out var goalStorageAmounts))
        {
            return;
        }

        SubtractLeasedAmountUnsafe(goalStorageAmounts, lease.Storage, lease.Amount);
        if (goalStorageAmounts.Count == 0)
        {
            ActiveLeasedAmountByGoal.Remove(lease.Goal);
        }
    }

    private static int GetGoalLeasedAmountUnsafe(IStorage storage, Goal goal)
    {
        return ActiveLeasedAmountByGoal.TryGetValue(goal, out var goalStorageAmounts) &&
               goalStorageAmounts.TryGetValue(storage, out var leasedAmount)
            ? leasedAmount
            : 0;
    }

    private static void AddLeasedAmountUnsafe(Dictionary<IStorage, int> leasedAmountsByStorage, IStorage storage, int amount)
    {
        if (storage == null || amount <= 0)
        {
            return;
        }

        leasedAmountsByStorage[storage] = leasedAmountsByStorage.TryGetValue(storage, out var currentAmount)
            ? currentAmount + amount
            : amount;
    }

    private static void SubtractLeasedAmountUnsafe(Dictionary<IStorage, int> leasedAmountsByStorage, IStorage storage, int amount)
    {
        if (storage == null || amount <= 0)
        {
            return;
        }

        if (!leasedAmountsByStorage.TryGetValue(storage, out var currentAmount))
        {
            return;
        }

        var nextAmount = currentAmount - amount;
        if (nextAmount > 0)
        {
            leasedAmountsByStorage[storage] = nextAmount;
        }
        else
        {
            leasedAmountsByStorage.Remove(storage);
        }
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
