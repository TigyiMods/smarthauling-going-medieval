using UnityEngine;

namespace SmartHauling.Runtime;

internal static class RouteOrderingOptimizer
{
    private const int MaxExactStops = 12;

    public static IReadOnlyList<T> OrderOptimal<T>(
        IReadOnlyList<T> items,
        Vector3 startPosition,
        Func<T, Vector3?> getPosition,
        Func<Vector3, T, float> getTransitionCost,
        Func<T, float> getFallbackSortKey)
    {
        if (items == null || items.Count <= 1)
        {
            return items ?? Array.Empty<T>();
        }

        var positioned = items
            .Select(item => new PositionedItem<T>(item, getPosition(item)))
            .Where(entry => entry.Position.HasValue)
            .ToList();
        var unpositioned = items
            .Where(item => !getPosition(item).HasValue)
            .OrderBy(getFallbackSortKey)
            .ToList();

        if (positioned.Count == 0)
        {
            return unpositioned;
        }

        var orderedPositioned = positioned.Count <= MaxExactStops
            ? OrderExact(positioned, startPosition, getTransitionCost)
            : OrderGreedy(positioned, startPosition, getTransitionCost);

        var ordered = new List<T>(items.Count);
        ordered.AddRange(orderedPositioned.Select(entry => entry.Item));
        ordered.AddRange(unpositioned);
        return ordered;
    }

    private static IReadOnlyList<PositionedItem<T>> OrderExact<T>(
        IReadOnlyList<PositionedItem<T>> items,
        Vector3 startPosition,
        Func<Vector3, T, float> getTransitionCost)
    {
        var count = items.Count;
        var fullMask = (1 << count) - 1;
        var stateCount = 1 << count;
        var costs = new float[stateCount, count];
        var parents = new int[stateCount, count];
        for (var mask = 0; mask < stateCount; mask++)
        {
            for (var last = 0; last < count; last++)
            {
                costs[mask, last] = float.MaxValue;
                parents[mask, last] = -1;
            }
        }

        for (var index = 0; index < count; index++)
        {
            var mask = 1 << index;
            costs[mask, index] = getTransitionCost(startPosition, items[index].Item);
        }

        for (var mask = 1; mask <= fullMask; mask++)
        {
            for (var last = 0; last < count; last++)
            {
                if ((mask & (1 << last)) == 0)
                {
                    continue;
                }

                var currentCost = costs[mask, last];
                if (currentCost >= float.MaxValue / 2f)
                {
                    continue;
                }

                var currentPosition = items[last].Position!.Value;
                var remainingMask = fullMask ^ mask;
                for (var next = 0; next < count; next++)
                {
                    var nextBit = 1 << next;
                    if ((remainingMask & nextBit) == 0)
                    {
                        continue;
                    }

                    var nextMask = mask | nextBit;
                    var transition = getTransitionCost(currentPosition, items[next].Item);
                    var candidateCost = currentCost + transition;
                    if (candidateCost + 0.0001f < costs[nextMask, next])
                    {
                        costs[nextMask, next] = candidateCost;
                        parents[nextMask, next] = last;
                    }
                }
            }
        }

        var bestLast = 0;
        var bestCost = float.MaxValue;
        for (var last = 0; last < count; last++)
        {
            var cost = costs[fullMask, last];
            if (cost < bestCost)
            {
                bestCost = cost;
                bestLast = last;
            }
        }

        var orderedIndexes = new List<int>(count);
        var walkMask = fullMask;
        var walkLast = bestLast;
        while (walkLast >= 0)
        {
            orderedIndexes.Add(walkLast);
            var parent = parents[walkMask, walkLast];
            walkMask &= ~(1 << walkLast);
            walkLast = parent;
        }

        orderedIndexes.Reverse();
        return orderedIndexes.Select(index => items[index]).ToList();
    }

    private static IReadOnlyList<PositionedItem<T>> OrderGreedy<T>(
        IReadOnlyList<PositionedItem<T>> items,
        Vector3 startPosition,
        Func<Vector3, T, float> getTransitionCost)
    {
        var remaining = items.ToList();
        var ordered = new List<PositionedItem<T>>(items.Count);
        var currentPosition = startPosition;

        while (remaining.Count > 0)
        {
            var next = remaining
                .OrderBy(item => getTransitionCost(currentPosition, item.Item))
                .First();
            ordered.Add(next);
            currentPosition = next.Position!.Value;
            remaining.Remove(next);
        }

        return ordered;
    }

    private readonly struct PositionedItem<T>
    {
        public PositionedItem(T item, Vector3? position)
        {
            Item = item;
            Position = position;
        }

        public T Item { get; }

        public Vector3? Position { get; }
    }
}
