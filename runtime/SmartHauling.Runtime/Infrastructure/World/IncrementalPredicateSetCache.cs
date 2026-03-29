namespace SmartHauling.Runtime.Infrastructure.World;

internal sealed class IncrementalPredicateSetCache<TItem> where TItem : class
{
    private readonly object syncRoot = new();
    private readonly IEqualityComparer<TItem> comparer;
    private readonly float sourceLifetimeSeconds;
    private readonly int sweepChunkSize;
    private readonly int coldStartSweepMultiplier;
    private readonly HashSet<TItem> matchingItems;
    private IReadOnlyList<TItem> sourceSnapshot = Array.Empty<TItem>();
    private IReadOnlyList<TItem> cachedMatchingSnapshot = Array.Empty<TItem>();
    private float sourceSnapshotExpiresAt;
    private int nextSweepIndex;
    private bool snapshotDirty;

    public IncrementalPredicateSetCache(
        float sourceLifetimeSeconds,
        int sweepChunkSize,
        int coldStartSweepMultiplier,
        IEqualityComparer<TItem>? comparer = null)
    {
        if (sourceLifetimeSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLifetimeSeconds));
        }

        if (sweepChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepChunkSize));
        }

        if (coldStartSweepMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coldStartSweepMultiplier));
        }

        this.comparer = comparer ?? EqualityComparer<TItem>.Default;
        this.sourceLifetimeSeconds = sourceLifetimeSeconds;
        this.sweepChunkSize = sweepChunkSize;
        this.coldStartSweepMultiplier = coldStartSweepMultiplier;
        matchingItems = new HashSet<TItem>(this.comparer);
    }

    public IReadOnlyList<TItem> GetSnapshot(
        float now,
        Func<IReadOnlyList<TItem>> getSourceSnapshot,
        Func<TItem, bool> shouldInclude)
    {
        if (getSourceSnapshot == null)
        {
            throw new ArgumentNullException(nameof(getSourceSnapshot));
        }

        if (shouldInclude == null)
        {
            throw new ArgumentNullException(nameof(shouldInclude));
        }

        lock (syncRoot)
        {
            if (sourceSnapshotExpiresAt <= now)
            {
                sourceSnapshot = getSourceSnapshot() ?? Array.Empty<TItem>();
                sourceSnapshotExpiresAt = now + sourceLifetimeSeconds;
                nextSweepIndex = 0;
                PruneRemovedItems();
            }

            AdvanceSweep(shouldInclude);
            if (snapshotDirty)
            {
                cachedMatchingSnapshot = matchingItems.ToList();
                snapshotDirty = false;
            }

            return cachedMatchingSnapshot;
        }
    }

    private void AdvanceSweep(Func<TItem, bool> shouldInclude)
    {
        if (sourceSnapshot.Count == 0)
        {
            if (matchingItems.Count > 0)
            {
                matchingItems.Clear();
                snapshotDirty = true;
            }

            return;
        }

        var itemsToProcess = sweepChunkSize;
        if (matchingItems.Count == 0 && nextSweepIndex == 0)
        {
            itemsToProcess *= coldStartSweepMultiplier;
        }

        itemsToProcess = Math.Min(itemsToProcess, sourceSnapshot.Count);
        for (var processed = 0; processed < itemsToProcess; processed++)
        {
            if (nextSweepIndex >= sourceSnapshot.Count)
            {
                nextSweepIndex = 0;
            }

            var item = sourceSnapshot[nextSweepIndex];
            if (shouldInclude(item))
            {
                if (matchingItems.Add(item))
                {
                    snapshotDirty = true;
                }
            }
            else if (matchingItems.Remove(item))
            {
                snapshotDirty = true;
            }

            nextSweepIndex++;
        }
    }

    private void PruneRemovedItems()
    {
        if (matchingItems.Count == 0)
        {
            return;
        }

        var liveSourceItems = new HashSet<TItem>(sourceSnapshot, comparer);
        if (matchingItems.RemoveWhere(item => !liveSourceItems.Contains(item)) > 0)
        {
            snapshotDirty = true;
        }
    }
}
