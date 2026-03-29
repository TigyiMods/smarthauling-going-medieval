namespace SmartHauling.Runtime.Tests;

public sealed class StorageCapacityEstimatorTests
{
    [Fact]
    public void ResolveAvailableCapacity_WhenProjectedCapacityIsLowerThanDirectCapacity_UsesConservativeEstimate()
    {
        // Arrange
        const int leasedAmount = 0;

        // Act
        var capacity = StorageCapacityEstimator.ResolveAvailableCapacity(
            leasedAmount,
            109,
            null,
            8);

        // Assert
        Assert.Equal(8, capacity);
    }

    [Fact]
    public void ResolveAvailableCapacity_WhenProjectedCapacityIsZero_DoesNotFallbackToRequestedAmount()
    {
        // Arrange
        const int leasedAmount = 0;

        // Act
        var capacity = StorageCapacityEstimator.ResolveAvailableCapacity(
            leasedAmount,
            null,
            null,
            0);

        // Assert
        Assert.Equal(0, capacity);
    }

    [Fact]
    public void ResolveAvailableCapacity_WhenOnlyFallbackExists_UsesSingleItemBudget()
    {
        // Arrange
        const int leasedAmount = 0;

        // Act
        var capacity = StorageCapacityEstimator.ResolveAvailableCapacity(
            leasedAmount,
            null,
            null,
            null);

        // Assert
        Assert.Equal(1, capacity);
    }

    [Fact]
    public void ResolveAvailableCapacity_WhenLeasesExist_SubtractsThemAfterChoosingConservativeEstimate()
    {
        // Arrange
        const int leasedAmount = 3;

        // Act
        var capacity = StorageCapacityEstimator.ResolveAvailableCapacity(
            leasedAmount,
            12,
            10,
            8);

        // Assert
        Assert.Equal(5, capacity);
    }
}
