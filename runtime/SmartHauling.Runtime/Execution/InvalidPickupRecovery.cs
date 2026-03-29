namespace SmartHauling.Runtime;

internal readonly struct InvalidPickupRecoveryPlan
{
    public InvalidPickupRecoveryPlan(bool releaseCurrentTarget, int queueIndexToDrop)
    {
        ReleaseCurrentTarget = releaseCurrentTarget;
        QueueIndexToDrop = queueIndexToDrop;
    }

    public bool ReleaseCurrentTarget { get; }

    public int QueueIndexToDrop { get; }

    public bool HasQueueTarget => QueueIndexToDrop >= 0;

    public bool HasAnyAction => ReleaseCurrentTarget || HasQueueTarget;
}

internal static class InvalidPickupRecovery
{
    public static InvalidPickupRecoveryPlan CreatePlan<T>(
        bool hasCurrentTarget,
        T? currentTarget,
        IReadOnlyList<T?> queuedTargets,
        IEqualityComparer<T>? comparer = null)
        where T : class
    {
        comparer ??= EqualityComparer<T>.Default;

        if (hasCurrentTarget && currentTarget != null)
        {
            for (var index = 0; index < queuedTargets.Count; index++)
            {
                if (queuedTargets[index] != null && comparer.Equals(queuedTargets[index]!, currentTarget))
                {
                    return new InvalidPickupRecoveryPlan(releaseCurrentTarget: true, queueIndexToDrop: index);
                }
            }

            return queuedTargets.Count > 0
                ? new InvalidPickupRecoveryPlan(releaseCurrentTarget: true, queueIndexToDrop: 0)
                : new InvalidPickupRecoveryPlan(releaseCurrentTarget: true, queueIndexToDrop: -1);
        }

        return queuedTargets.Count > 0
            ? new InvalidPickupRecoveryPlan(releaseCurrentTarget: false, queueIndexToDrop: 0)
            : new InvalidPickupRecoveryPlan(releaseCurrentTarget: false, queueIndexToDrop: -1);
    }
}
