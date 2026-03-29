using NSMedieval.State;
using SmartHauling.Runtime.Patches;
using UnityEngine;

namespace SmartHauling.Runtime.Tests;

public sealed class PickupRouteOrderingTests
{
    [Fact]
    public void OrderCandidates_WhenPilesFormTwoNearbyClusters_PrefersLocalClusterBeforeJumpingAway()
    {
        // Arrange
        var first = CreatePile(new Vector3(1f, 0f, 0f));
        var second = CreatePile(new Vector3(2f, 0f, 0f));
        var third = CreatePile(new Vector3(10f, 0f, 0f));
        var fourth = CreatePile(new Vector3(11f, 0f, 0f));

        // Act
        var ordered = PickupRouteOrdering.OrderCandidates(
            new[] { first, third, second, fourth },
            startPosition: Vector3.zero,
            targetPosition: new Vector3(20f, 0f, 0f));

        // Assert
        Assert.Equal(new[] { first, second, third, fourth }, ordered);
    }

    private static ResourcePileInstance CreatePile(Vector3 position)
    {
        var pile = A.Fake<ResourcePileInstance>();
        A.CallTo(() => pile.GetPosition()).Returns(position);
        return pile;
    }
}
