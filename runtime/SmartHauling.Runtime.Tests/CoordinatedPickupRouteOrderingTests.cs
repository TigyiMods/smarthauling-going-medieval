using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class CoordinatedPickupRouteOrderingTests
{
    [Fact]
    public void OrderCandidates_WhenLaterPickupSharesDownstreamDirection_PrefersItBeforeOppositeCluster()
    {
        // Arrange
        var first = new PickupCandidate("wood", new Vector3(1f, 0f, 0f));
        var second = new PickupCandidate("wood", new Vector3(2f, 0f, 0f));
        var third = new PickupCandidate("sticks", new Vector3(-1f, 0f, 0f));
        var dropAnchorsByResourceId = new Dictionary<string, IReadOnlyList<Vector3>>
        {
            ["wood"] = new[] { new Vector3(20f, 0f, 0f) },
            ["sticks"] = new[] { new Vector3(-20f, 0f, 0f) }
        };

        // Act
        var ordered = CoordinatedPickupRouteOrdering.OrderCandidates(
            new[] { first, third, second },
            candidate => candidate.ResourceId,
            candidate => candidate.Position,
            dropAnchorsByResourceId);

        // Assert
        Assert.Equal(new[] { first, second, third }, ordered);
    }

    [Fact]
    public void OrderCandidates_WhenResourceHasMultipleDropAnchors_UsesBestAnchorForCurrentLeg()
    {
        // Arrange
        var first = new PickupCandidate("wood", Vector3.zero);
        var northCandidate = new PickupCandidate("sticks", new Vector3(2f, 0f, 0f));
        var southCandidate = new PickupCandidate("wood", new Vector3(-5f, 0f, 0f));
        var dropAnchorsByResourceId = new Dictionary<string, IReadOnlyList<Vector3>>
        {
            ["wood"] = new[] { new Vector3(20f, 0f, 0f), new Vector3(-20f, 0f, 0f) },
            ["sticks"] = new[] { new Vector3(20f, 0f, 0f) }
        };

        // Act
        var ordered = CoordinatedPickupRouteOrdering.OrderCandidates(
            new[] { first, northCandidate, southCandidate },
            candidate => candidate.ResourceId,
            candidate => candidate.Position,
            dropAnchorsByResourceId);

        // Assert
        Assert.Equal(new[] { first, southCandidate, northCandidate }, ordered);
    }

    [Fact]
    public void BuildDropOrder_WhenPickupRouteMixesResources_UsesPickupRouteFirstOccurrence()
    {
        // Arrange
        var primaryResourceId = "wood";
        var orderedPickups = new[]
        {
            new PickupCandidate("wood", new Vector3(1f, 0f, 0f)),
            new PickupCandidate("cabbage", new Vector3(2f, 0f, 0f)),
            new PickupCandidate("sticks", new Vector3(3f, 0f, 0f)),
            new PickupCandidate("cabbage", new Vector3(4f, 0f, 0f))
        };
        var allResourceIds = new[] { "wood", "sticks", "cabbage", "hay" };

        // Act
        var dropOrder = CoordinatedPickupRouteOrdering.BuildDropOrder(
            primaryResourceId,
            orderedPickups,
            candidate => candidate.ResourceId,
            allResourceIds);

        // Assert
        Assert.Equal(new[] { "wood", "cabbage", "sticks", "hay" }, dropOrder);
    }

    private sealed record PickupCandidate(string ResourceId, Vector3 Position);
}
