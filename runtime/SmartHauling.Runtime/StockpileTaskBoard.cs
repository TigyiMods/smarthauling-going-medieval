using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NSEipix;
using NSEipix.Base;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.State;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class StockpileTaskBoard
{
    private const float TaskLeaseSeconds = 18f;
    private const float TaskFailureCooldownSeconds = 10f;
    private const float TaskSnapshotLifetimeSeconds = 1f;
    private const float FallbackNominalWorkerFreeSpace = 120f;

    private static readonly PropertyInfo? CreaturesProperty =
        AccessTools.Property(typeof(CreatureManager), "Creatures");

    private static readonly FieldInfo? CreaturesField =
        AccessTools.Field(typeof(CreatureManager), "creatures");

    private static readonly object SyncRoot = new();
    private static readonly List<TaskLease> ActiveLeases = new();
    private static readonly Dictionary<ResourcePileInstance, StockpileTaskSeed> PendingTasks =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private static readonly Dictionary<ResourcePileInstance, float> FailedUntil =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private static readonly Dictionary<CreatureBase, StockpileTaskSeed> PendingAssignments =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);
    private static float SnapshotExpiresAt;

    public static bool TryAssign(
        Goal goal,
        CreatureBase creature,
        out HaulingDecisionTracePatch.PlannedSeedSelection? selected)
    {
        selected = null;
        if (goal is not StockpileHaulingGoal stockpileGoal || creature == null)
        {
            return false;
        }

        lock (SyncRoot)
        {
            Cleanup();
            EnsureSnapshot();
            RebuildAssignments();

            var best = SelectBestMaterializedPlan(stockpileGoal, creature);
            if (best == null)
            {
                PendingAssignments.Remove(creature);
                RebuildAssignments();
                best = SelectBestMaterializedPlan(stockpileGoal, creature);
                if (best == null)
                {
                    return false;
                }
            }

            var leasedSeed = PendingTasks.TryGetValue(best.FirstPile, out var seed) ? seed : null;
            ActiveLeases.Add(new TaskLease(
                goal,
                creature,
                best.FirstPile,
                leasedSeed?.SourcePatchPiles ?? new[] { best.FirstPile },
                Time.realtimeSinceStartup + TaskLeaseSeconds));
            DiagnosticTrace.Info(
                "haul.plan",
                $"Board assigned task {best.FirstPile.BlueprintId} to {creature}: taskScore={best.Score:0.0}, claimScore={HaulingDecisionTracePatch.GetBoardClaimScore(creature, best):0.0}",
                80);
            selected = best;
            return true;
        }
    }

    public static bool HasAssignableTask(WorkerGoapAgent workerAgent)
    {
        if (workerAgent?.AgentOwner is not CreatureBase creature ||
            creature.HasDisposed ||
            workerAgent.GetCurrentGoal() != null ||
            workerAgent.IsGoalPreparing)
        {
            return false;
        }

        if (creature is IStorageAgent { Storage: not null } storageAgent &&
            !storageAgent.Storage.IsEmpty())
        {
            return false;
        }

        lock (SyncRoot)
        {
            Cleanup();
            EnsureSnapshot();
            RebuildAssignments();

            var probeGoal = new StockpileHaulingGoal(workerAgent);
            try
            {
                return SelectBestMaterializedPlan(probeGoal, creature) != null;
            }
            finally
            {
                probeGoal.Dispose();
            }
        }
    }

    internal static float GetNominalWorkerFreeSpace()
    {
        var manager = MonoSingleton<CreatureManager>.Instance;
        if (manager == null)
        {
            return FallbackNominalWorkerFreeSpace;
        }

        var freeSpaces = new List<float>();
        foreach (var creature in GetCreatureEnumerable(manager).OfType<CreatureBase>())
        {
            if (creature == null || creature.HasDisposed)
            {
                continue;
            }

            if (creature is not IGoapAgentOwner goapOwner ||
                goapOwner.GetGoapAgent() is not WorkerGoapAgent workerAgent ||
                workerAgent.HasDisposed)
            {
                continue;
            }

            if (creature is not IStorageAgent { Storage: not null } storageAgent)
            {
                continue;
            }

            var freeSpace = storageAgent.Storage.GetFreeSpace();
            if (freeSpace > 0f)
            {
                freeSpaces.Add(freeSpace);
            }
        }

        if (freeSpaces.Count == 0)
        {
            return FallbackNominalWorkerFreeSpace;
        }

        freeSpaces.Sort();
        return freeSpaces[freeSpaces.Count / 2];
    }

    private static HaulingDecisionTracePatch.PlannedSeedSelection? SelectBestMaterializedPlan(
        StockpileHaulingGoal goal,
        CreatureBase creature)
    {
        var candidateSeeds = new List<StockpileTaskSeed>();
        if (PendingAssignments.TryGetValue(creature, out var assignedSeed) &&
            assignedSeed?.FirstPile != null &&
            CanUseCandidate(assignedSeed, goal))
        {
            candidateSeeds.Add(assignedSeed);
        }

        candidateSeeds.AddRange(PendingTasks.Values
            .Where(seed =>
                seed?.FirstPile != null &&
                CanUseCandidate(seed, goal) &&
                candidateSeeds.All(existing => !ReferenceEquals(existing.FirstPile, seed.FirstPile)))
            .OrderByDescending(seed => seed.Score));

        HaulingDecisionTracePatch.PlannedSeedSelection? best = null;
        var bestScore = float.MinValue;
        foreach (var seed in candidateSeeds)
        {
            var materialized = HaulingDecisionTracePatch.TryMaterializeBoardSelection(goal, creature, seed);
            if (materialized == null)
            {
                continue;
            }

            var score = HaulingDecisionTracePatch.GetBoardClaimScore(creature, materialized);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = materialized;
        }

        return best;
    }

    public static void RefreshGoal(Goal goal)
    {
        if (goal == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            Cleanup();
            var expiresAt = Time.realtimeSinceStartup + TaskLeaseSeconds;
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
            var releasedPiles = ActiveLeases
                .Where(lease => ReferenceEquals(lease.Goal, goal))
                .Select(lease => lease.FirstPile)
                .ToList();
            ActiveLeases.RemoveAll(lease => ReferenceEquals(lease.Goal, goal));
            var releasedOwners = PendingAssignments
                .Where(entry => entry.Key == null || ActiveLeases.All(lease => !ReferenceEquals(lease.Owner, entry.Key)))
                .Select(entry => entry.Key)
                .ToList();
            foreach (var owner in releasedOwners)
            {
                PendingAssignments.Remove(owner);
            }
            foreach (var pile in releasedPiles)
            {
                PendingTasks.Remove(pile);
            }
            Cleanup();
        }
    }

    public static void MarkFailed(ResourcePileInstance pile)
    {
        if (pile == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            Cleanup();
            ActiveLeases.RemoveAll(lease => ReferenceEquals(lease.FirstPile, pile));
            PendingTasks.Remove(pile);
            var impactedOwners = PendingAssignments
                .Where(entry => ReferenceEquals(entry.Value?.FirstPile, pile))
                .Select(entry => entry.Key)
                .ToList();
            foreach (var owner in impactedOwners)
            {
                PendingAssignments.Remove(owner);
            }
            FailedUntil[pile] = Time.realtimeSinceStartup + TaskFailureCooldownSeconds;
        }
    }

    private static bool CanUseCandidate(StockpileTaskSeed candidate, Goal? goal)
    {
        var pile = candidate.FirstPile;
        if (pile == null || pile.HasDisposed)
        {
            return false;
        }

        if (FailedUntil.TryGetValue(pile, out var blockedUntil) && blockedUntil > Time.realtimeSinceStartup)
        {
            return false;
        }

        return ActiveLeases.All(lease =>
            !lease.SourcePatchPiles.Any(patchPile => candidate.SourcePatchPiles.Any(candidatePile => ReferenceEquals(candidatePile, patchPile))) ||
            ReferenceEquals(lease.Goal, goal));
    }

    private static void EnsureSnapshot()
    {
        var now = Time.realtimeSinceStartup;
        if (PendingTasks.Count > 0 && SnapshotExpiresAt > now)
        {
            return;
        }

        PendingTasks.Clear();
        foreach (var candidate in HaulingDecisionTracePatch.BuildTaskSnapshotForBoard())
        {
            if (candidate?.FirstPile == null || candidate.FirstPile.HasDisposed)
            {
                continue;
            }

            if (FailedUntil.TryGetValue(candidate.FirstPile, out var blockedUntil) && blockedUntil > now)
            {
                continue;
            }

            PendingTasks[candidate.FirstPile] = candidate;
        }

        SnapshotExpiresAt = now + TaskSnapshotLifetimeSeconds;
        DiagnosticTrace.Info(
            "haul.plan",
            $"Board snapshot refreshed: tasks={PendingTasks.Count}, top=[{HaulingDecisionTracePatch.DescribeTaskSeeds(PendingTasks.Values)}]",
            40);
    }

    private static void RebuildAssignments()
    {
        PendingAssignments.Clear();

        var availableSeeds = PendingTasks.Values
            .Where(candidate => candidate?.FirstPile != null && CanUseCandidate(candidate, null))
            .ToList();
        if (availableSeeds.Count == 0)
        {
            return;
        }

        var workers = GetAssignableWorkers().ToList();
        if (workers.Count == 0)
        {
            return;
        }

        var remainingWorkers = new HashSet<CreatureBase>(workers, ReferenceEqualityComparer<CreatureBase>.Instance);
        var remainingSeeds = new List<StockpileTaskSeed>(availableSeeds);

        while (remainingWorkers.Count > 0 && remainingSeeds.Count > 0)
        {
            CreatureBase? bestWorker = null;
            StockpileTaskSeed? bestSeed = null;
            var bestScore = float.MinValue;

            foreach (var worker in remainingWorkers)
            {
                foreach (var seed in remainingSeeds)
                {
                    var score = GetAssignmentScore(worker, seed);
                    if (score <= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestWorker = worker;
                    bestSeed = seed;
                }
            }

            if (bestWorker == null || bestSeed == null)
            {
                break;
            }

            PendingAssignments[bestWorker] = bestSeed;
            remainingWorkers.Remove(bestWorker);
            remainingSeeds.RemoveAll(seed => SharesSourcePatch(seed, bestSeed));
        }

        DiagnosticTrace.Info(
            "haul.plan",
            $"Board assignments rebuilt: workers={workers.Count}, assigned={PendingAssignments.Count}, seeds={availableSeeds.Count}",
            40);
    }

    private static IEnumerable<CreatureBase> GetAssignableWorkers()
    {
        var manager = MonoSingleton<CreatureManager>.Instance;
        if (manager == null)
        {
            yield break;
        }

        var seen = new HashSet<CreatureBase>(ReferenceEqualityComparer<CreatureBase>.Instance);
        foreach (var creature in GetCreatureEnumerable(manager).OfType<CreatureBase>())
        {
            if (creature == null || creature.HasDisposed || !seen.Add(creature))
            {
                continue;
            }

            if (creature is not IGoapAgentOwner goapOwner ||
                goapOwner.GetGoapAgent() is not WorkerGoapAgent workerAgent ||
                workerAgent.HasDisposed)
            {
                continue;
            }

            var currentGoal = workerAgent.GetCurrentGoal();
            if (currentGoal != null)
            {
                continue;
            }

            if (creature is IStorageAgent { Storage: not null } storageAgent &&
                !storageAgent.Storage.IsEmpty())
            {
                continue;
            }

            yield return creature;
        }
    }

    private static IEnumerable<object> GetCreatureEnumerable(CreatureManager manager)
    {
        if (CreaturesProperty?.GetValue(manager) is System.Collections.IEnumerable creaturesByProperty)
        {
            foreach (var creature in creaturesByProperty)
            {
                yield return creature;
            }

            yield break;
        }

        if (CreaturesField?.GetValue(manager) is System.Collections.IEnumerable creaturesByField)
        {
            foreach (var creature in creaturesByField)
            {
                yield return creature;
            }
        }
    }

    private static float GetAssignmentScore(CreatureBase creature, StockpileTaskSeed seed)
    {
        var distanceToSource = Vector3.Distance(creature.GetPosition(), seed.FirstPile.GetPosition());
        return HaulingScore.CalculateBoardAssignmentScore(seed.Score, distanceToSource);
    }

    private static bool SharesSourcePatch(StockpileTaskSeed left, StockpileTaskSeed right)
    {
        return left.SourcePatchPiles.Any(leftPile =>
            right.SourcePatchPiles.Any(rightPile => ReferenceEquals(leftPile, rightPile)));
    }

    private static void Cleanup()
    {
        var now = Time.realtimeSinceStartup;
        ActiveLeases.RemoveAll(lease =>
            lease.Goal == null ||
            lease.Owner == null ||
            lease.Owner.HasDisposed ||
            lease.FirstPile == null ||
            lease.FirstPile.HasDisposed ||
            lease.ExpiresAt <= now);

        var leasedPiles = new HashSet<ResourcePileInstance>(
            ActiveLeases.SelectMany(lease => lease.SourcePatchPiles),
            ReferenceEqualityComparer<ResourcePileInstance>.Instance);

        var expiredTasks = PendingTasks
            .Where(entry =>
                entry.Key == null ||
                entry.Key.HasDisposed ||
                (!leasedPiles.Contains(entry.Key) && SnapshotExpiresAt <= now))
            .Select(entry => entry.Key)
            .ToList();
        foreach (var pile in expiredTasks)
        {
            PendingTasks.Remove(pile);
        }

        var expiredAssignments = PendingAssignments
            .Where(entry =>
                entry.Key == null ||
                entry.Key.HasDisposed ||
                entry.Value == null ||
                entry.Value.FirstPile == null ||
                entry.Value.FirstPile.HasDisposed ||
                !PendingTasks.ContainsKey(entry.Value.FirstPile))
            .Select(entry => entry.Key)
            .ToList();
        foreach (var owner in expiredAssignments)
        {
            PendingAssignments.Remove(owner);
        }

        CleanupFailed();
    }

    private static void CleanupFailed()
    {
        var now = Time.realtimeSinceStartup;
        var expired = FailedUntil
            .Where(entry => entry.Key == null || entry.Key.HasDisposed || entry.Value <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var pile in expired)
        {
            FailedUntil.Remove(pile);
        }
    }

    private sealed class TaskLease
    {
        public TaskLease(
            Goal goal,
            CreatureBase owner,
            ResourcePileInstance firstPile,
            IEnumerable<ResourcePileInstance> sourcePatchPiles,
            float expiresAt)
        {
            Goal = goal;
            Owner = owner;
            FirstPile = firstPile;
            SourcePatchPiles = sourcePatchPiles
                .Where(pile => pile != null)
                .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance)
                .ToArray();
            ExpiresAt = expiresAt;
        }

        public Goal Goal { get; }

        public CreatureBase Owner { get; }

        public ResourcePileInstance FirstPile { get; }

        public IReadOnlyList<ResourcePileInstance> SourcePatchPiles { get; }

        public float ExpiresAt { get; set; }
    }
}
