using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class RouteOrderingOptimizerTests
{
    [Fact]
    public void OrderOptimal_WhenNearestNeighborIsTrap_UsesLowerGlobalCostPath()
    {
        // Arrange
        var candidates = new[]
        {
            new RouteNode("A"),
            new RouteNode("B"),
            new RouteNode("C")
        };

        var costs = new Dictionary<(string From, string To), float>
        {
            [("start", "A")] = 1f,
            [("start", "B")] = 2f,
            [("start", "C")] = 2f,
            [("A", "B")] = 100f,
            [("A", "C")] = 100f,
            [("B", "A")] = 1f,
            [("B", "C")] = 1f,
            [("C", "A")] = 1f,
            [("C", "B")] = 1f
        };

        // Act
        var ordered = RouteOrderingOptimizer.OrderOptimal(
            candidates,
            Vector3.zero,
            candidate => candidate.Position,
            (currentPosition, candidate) =>
            {
                var from = currentPosition.x switch
                {
                    1f => "A",
                    2f => "B",
                    3f => "C",
                    _ => "start"
                };
                return costs[(from, candidate.Id)];
            },
            _ => 0f);
        var orderedCost = CalculateRouteCost(ordered, costs);
        var greedyTrapCost = costs[("start", "A")] + costs[("A", "B")] + costs[("B", "C")];

        // Assert
        Assert.Equal(4f, orderedCost);
        Assert.True(orderedCost < greedyTrapCost);
        Assert.NotEqual("A", ordered[0].Id);
    }

    private static float CalculateRouteCost(
        IReadOnlyList<RouteNode> ordered,
        IReadOnlyDictionary<(string From, string To), float> costs)
    {
        var total = 0f;
        var from = "start";
        foreach (var node in ordered)
        {
            total += costs[(from, node.Id)];
            from = node.Id;
        }

        return total;
    }

    private sealed class RouteNode
    {
        public RouteNode(string id)
        {
            Id = id;
            Position = id switch
            {
                "A" => new Vector3(1f, 0f, 0f),
                "B" => new Vector3(2f, 0f, 0f),
                "C" => new Vector3(3f, 0f, 0f),
                _ => Vector3.zero
            };
        }

        public string Id { get; }

        public Vector3 Position { get; }
    }
}
