using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Patches;

namespace SmartHauling.Runtime;

/// <summary>
/// Central stockpile hauling coordinator that maintains the shared task snapshot and worker leases.
/// </summary>
/// <remarks>
/// This is the main orchestration point between high-level planning and per-worker execution.
/// It decides which worker may materialize which stockpile seed into a concrete hauling task.
/// </remarks>
internal static class StockpileTaskBoard
{
    private const float TaskLeaseSeconds = 18f;
    private const float TaskFailureCooldownSeconds = 10f;
    private const float TaskSnapshotLifetimeSeconds = 1f;
    private const float AvailabilityProbeCacheLifetimeSeconds = 0.25f;
    private const float FallbackNominalWorkerFreeSpace = 120f;

    private static readonly object SyncRoot = new();
    private static readonly List<StockpileTaskLease> ActiveLeases = new();
    private static readonly List<ResourcePileInstance> PendingUrgentPiles = new();
    private static readonly Dictionary<ResourcePileInstance, StockpileTaskSeed> PendingTasks =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private static readonly Dictionary<ResourcePileInstance, float> FailedUntil =
        new(ReferenceEqualityComparer<ResourcePileInstance>.Instance);
    private static readonly Dictionary<CreatureBase, StockpileTaskSeed> PendingAssignments =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);
    private static readonly Dictionary<CreatureBase, TimedAvailabilityProbeCache.Entry> CachedAvailabilityByWorker =
        new(ReferenceEqualityComparer<CreatureBase>.Instance);
    private static float SnapshotExpiresAt;
    private static int assignmentStateVersion;
    private static bool assignmentsDirty = true;

    /// <summary>
    /// Attempts to lease the best currently assignable stockpile task for the given creature and goal.
    /// </summary>
    public static bool TryAssign(
        Goal goal,
        CreatureBase creature,
        out PlannedSeedSelection? selected)
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
            EnsureAssignments();

            var best = SelectBestMaterializedPlan(stockpileGoal, creature);
            if (best == null)
            {
                PendingAssignments.Remove(creature);
                MarkAssignmentsDirty();
                EnsureAssignments();
                best = SelectBestMaterializedPlan(stockpileGoal, creature);
                if (best == null)
                {
                    return false;
                }
            }

            var leasedSeed = PendingTasks.TryGetValue(best.FirstPile, out var seed) ? seed : null;
            ActiveLeases.Add(new StockpileTaskLease(
                goal,
                creature,
                best.FirstPile,
                leasedSeed?.SourcePatchPiles ?? new[] { best.FirstPile },
                RuntimeServices.Clock.RealtimeSinceStartup + TaskLeaseSeconds));
            MarkAssignmentsDirty();
            DiagnosticTrace.Info(
                "haul.plan",
                $"Board assigned task {best.FirstPile.BlueprintId} to {creature}: taskScore={best.Score:0.0}, claimScore={HaulingDecisionTracePatch.GetBoardClaimScore(creature, best):0.0}",
                80);
            selected = best;
            return true;
        }
    }

    /// <summary>
    /// Returns whether the board currently has any assignable task for the worker's hauling context.
    /// </summary>
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
            var now = RuntimeServices.Clock.RealtimeSinceStartup;
            Cleanup();
            EnsureSnapshot();
            EnsureAssignments();

            if (CachedAvailabilityByWorker.TryGetValue(creature, out var cachedAvailability) &&
                TimedAvailabilityProbeCache.TryGet(cachedAvailability, assignmentStateVersion, now, out var cachedValue))
            {
                return cachedValue;
            }

            var probeGoal = new StockpileHaulingGoal(workerAgent);
            try
            {
                var hasAssignableTask = SelectBestMaterializedPlan(probeGoal, creature) != null;
                CachedAvailabilityByWorker[creature] = TimedAvailabilityProbeCache.Create(
                    assignmentStateVersion,
                    now,
                    AvailabilityProbeCacheLifetimeSeconds,
                    hasAssignableTask);
                return hasAssignableTask;
            }
            finally
            {
                probeGoal.Dispose();
            }
        }
    }

    public static bool HasPendingUrgentTask()
    {
        lock (SyncRoot)
        {
            Cleanup();
            EnsureSnapshot();
            return PendingUrgentPiles.Count > 0;
        }
    }

    public static IReadOnlyList<ResourcePileInstance> GetPendingUrgentPilesSnapshot()
    {
        lock (SyncRoot)
        {
            Cleanup();
            EnsureSnapshot();
            return PendingUrgentPiles.ToArray();
        }
    }

    internal static float GetNominalWorkerFreeSpace()
    {
        var freeSpaces = new List<float>();
        foreach (var creature in RuntimeServices.WorldSnapshot.GetCreatures())
        {
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

    private static PlannedSeedSelection? SelectBestMaterializedPlan(
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

        PlannedSeedSelection? best = null;
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
            var expiresAt = RuntimeServices.Clock.RealtimeSinceStartup + TaskLeaseSeconds;
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
            if (releasedPiles.Count > 0)
            {
                RebuildPendingUrgentSnapshot();
            }
            if (releasedPiles.Count > 0 || releasedOwners.Count > 0)
            {
                MarkAssignmentsDirty();
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
            RebuildPendingUrgentSnapshot();
            var impactedOwners = PendingAssignments
                .Where(entry => ReferenceEquals(entry.Value?.FirstPile, pile))
                .Select(entry => entry.Key)
                .ToList();
            foreach (var owner in impactedOwners)
            {
                PendingAssignments.Remove(owner);
            }
            FailedUntil[pile] = RuntimeServices.Clock.RealtimeSinceStartup + TaskFailureCooldownSeconds;
            MarkAssignmentsDirty();
        }
    }

    private static bool CanUseCandidate(StockpileTaskSeed candidate, Goal? goal)
    {
        var pile = candidate.FirstPile;
        if (pile == null || pile.HasDisposed)
        {
            return false;
        }

        if (FailedUntil.TryGetValue(pile, out var blockedUntil) && blockedUntil > RuntimeServices.Clock.RealtimeSinceStartup)
        {
            return false;
        }

        return ActiveLeases.All(lease =>
            !lease.SourcePatchPiles.Any(patchPile => candidate.SourcePatchPiles.Any(candidatePile => ReferenceEquals(candidatePile, patchPile))) ||
            ReferenceEquals(lease.Goal, goal));
    }

    private static void EnsureSnapshot()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
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

        RebuildPendingUrgentSnapshot();
        SnapshotExpiresAt = now + TaskSnapshotLifetimeSeconds;
        MarkAssignmentsDirty();
        DiagnosticTrace.Info(
            "haul.plan",
            $"Board snapshot refreshed: tasks={PendingTasks.Count}, top=[{HaulingDecisionTracePatch.DescribeTaskSeeds(PendingTasks.Values)}]",
            40);
    }

    private static void EnsureAssignments()
    {
        if (!assignmentsDirty)
        {
            return;
        }

        PendingAssignments.Clear();
        foreach (var assignment in StockpileTaskAssignmentPlanner.BuildAssignments(
                     PendingTasks.Values,
                     candidate => CanUseCandidate(candidate, null)))
        {
            PendingAssignments[assignment.Key] = assignment.Value;
        }
        assignmentsDirty = false;

        DiagnosticTrace.Info(
            "haul.plan",
            $"Board assignments rebuilt: assigned={PendingAssignments.Count}, seeds={PendingTasks.Count}",
            40);
    }

    private static void Cleanup()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var hadChanges = false;
        ActiveLeases.RemoveAll(lease =>
        {
            var shouldRemove =
            lease.Goal == null ||
            lease.Owner == null ||
            lease.Owner.HasDisposed ||
            lease.FirstPile == null ||
            lease.FirstPile.HasDisposed ||
            lease.ExpiresAt <= now;
            hadChanges |= shouldRemove;
            return shouldRemove;
        });

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
            hadChanges = true;
        }
        if (expiredTasks.Count > 0)
        {
            RebuildPendingUrgentSnapshot();
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
            CachedAvailabilityByWorker.Remove(owner);
            hadChanges = true;
        }

        CleanupFailed();
        if (hadChanges)
        {
            MarkAssignmentsDirty();
        }
    }

    private static void CleanupFailed()
    {
        var now = RuntimeServices.Clock.RealtimeSinceStartup;
        var expired = FailedUntil
            .Where(entry => entry.Key == null || entry.Key.HasDisposed || entry.Value <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var pile in expired)
        {
            FailedUntil.Remove(pile);
        }

        if (expired.Count > 0)
        {
            MarkAssignmentsDirty();
        }
    }

    private static void MarkAssignmentsDirty()
    {
        assignmentsDirty = true;
        assignmentStateVersion++;
        CachedAvailabilityByWorker.Clear();
    }

    private static void RebuildPendingUrgentSnapshot()
    {
        PendingUrgentPiles.Clear();
        foreach (var pile in PendingTasks.Values
                     .SelectMany(seed => seed.SourcePatchPiles)
                     .Where(IsPendingUrgentPile)
                     .Distinct(ReferenceEqualityComparer<ResourcePileInstance>.Instance))
        {
            PendingUrgentPiles.Add(pile);
        }
    }

    private static bool IsPendingUrgentPile(ResourcePileInstance? pile)
    {
        return pile != null &&
               !pile.HasDisposed &&
               pile.IsUrgentHaul &&
               pile.OwnedByPlayer();
    }

}
