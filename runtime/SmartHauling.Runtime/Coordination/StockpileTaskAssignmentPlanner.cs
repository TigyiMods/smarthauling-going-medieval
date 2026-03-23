using NSMedieval.Goap;
using NSMedieval.State;
using SmartHauling.Runtime.Composition;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class StockpileTaskAssignmentPlanner
{
    public static Dictionary<CreatureBase, StockpileTaskSeed> BuildAssignments(
        IEnumerable<StockpileTaskSeed> pendingTasks,
        Func<StockpileTaskSeed, bool> canUseCandidate)
    {
        var assignments = new Dictionary<CreatureBase, StockpileTaskSeed>(ReferenceEqualityComparer<CreatureBase>.Instance);
        var availableSeeds = pendingTasks
            .Where(candidate => candidate?.FirstPile != null && canUseCandidate(candidate))
            .ToList();
        if (availableSeeds.Count == 0)
        {
            return assignments;
        }

        var workers = GetAssignableWorkers().ToList();
        if (workers.Count == 0)
        {
            return assignments;
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

            assignments[bestWorker] = bestSeed;
            remainingWorkers.Remove(bestWorker);
            remainingSeeds.RemoveAll(seed => SharesSourcePatch(seed, bestSeed));
        }

        return assignments;
    }

    private static IEnumerable<CreatureBase> GetAssignableWorkers()
    {
        var seen = new HashSet<CreatureBase>(ReferenceEqualityComparer<CreatureBase>.Instance);
        foreach (var creature in RuntimeServices.WorldSnapshot.GetCreatures())
        {
            if (!seen.Add(creature))
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
}
