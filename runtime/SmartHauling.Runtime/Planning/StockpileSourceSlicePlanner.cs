using UnityEngine;

namespace SmartHauling.Runtime;

internal sealed class SourceSliceCandidate<TId>
{
    public SourceSliceCandidate(TId id, Vector3 position, int amount, float nominalWeight)
    {
        Id = id;
        Position = position;
        Amount = amount;
        NominalWeight = nominalWeight;
    }

    public TId Id { get; }

    public Vector3 Position { get; }

    public int Amount { get; }

    public float NominalWeight { get; }
}

internal static class StockpileSourceSlicePlanner
{
    public static float GetNominalSourceSliceWeightBudget(
        float nominalWorkerFreeSpace,
        float minimumSourceSliceWeightBudget,
        bool treatAsSingleHeavyItem,
        float sampleWeight)
    {
        var workerBudget = Mathf.Max(minimumSourceSliceWeightBudget, nominalWorkerFreeSpace);
        if (!treatAsSingleHeavyItem)
        {
            return workerBudget;
        }

        return Mathf.Max(1f, sampleWeight);
    }

    public static float GetNominalPileWeight(
        bool hasBlueprint,
        bool treatAsSingleHeavyItem,
        float unitWeight,
        int amount)
    {
        if (!hasBlueprint)
        {
            return Mathf.Max(1f, amount);
        }

        if (treatAsSingleHeavyItem)
        {
            return Mathf.Max(1f, unitWeight);
        }

        var safeUnitWeight = Mathf.Max(0.01f, unitWeight);
        return Mathf.Max(safeUnitWeight, amount * safeUnitWeight);
    }

    public static IReadOnlyList<TId> BuildSlice<TId>(
        TId firstId,
        Vector3 firstPosition,
        IEnumerable<SourceSliceCandidate<TId>> candidates,
        float sliceBudgetWeight,
        IEqualityComparer<TId>? comparer = null)
    {
        comparer ??= EqualityComparer<TId>.Default;
        var orderedCandidates = candidates
            .Where(candidate => candidate != null && candidate.NominalWeight > 0f)
            .OrderBy(candidate => comparer.Equals(candidate.Id, firstId) ? 0 : 1)
            .ThenBy(candidate => Vector3.Distance(firstPosition, candidate.Position))
            .ThenByDescending(candidate => candidate.Amount)
            .ToList();

        var slice = new List<TId>();
        var accumulatedWeight = 0f;
        foreach (var candidate in orderedCandidates)
        {
            if (slice.Count > 0 && accumulatedWeight >= sliceBudgetWeight)
            {
                break;
            }

            slice.Add(candidate.Id);
            var remainingWeight = Mathf.Max(0f, sliceBudgetWeight - accumulatedWeight);
            accumulatedWeight += slice.Count == 1
                ? Mathf.Min(candidate.NominalWeight, sliceBudgetWeight)
                : Mathf.Min(candidate.NominalWeight, Mathf.Max(0.01f, remainingWeight));

            if (accumulatedWeight >= sliceBudgetWeight)
            {
                break;
            }
        }

        if (slice.Count == 0)
        {
            slice.Add(firstId);
        }

        return slice;
    }
}
