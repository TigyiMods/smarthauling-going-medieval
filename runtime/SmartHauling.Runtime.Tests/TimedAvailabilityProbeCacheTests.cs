namespace SmartHauling.Runtime.Tests;

public sealed class TimedAvailabilityProbeCacheTests
{
    [Fact]
    public void TryGet_WhenVersionMatchesAndEntryIsFresh_ReturnsCachedValue()
    {
        // Arrange
        var entry = TimedAvailabilityProbeCache.Create(version: 3, now: 10f, lifetimeSeconds: 0.25f, value: true);

        // Act
        var found = TimedAvailabilityProbeCache.TryGet(entry, version: 3, now: 10.1f, out var value);

        // Assert
        Assert.True(found);
        Assert.True(value);
    }

    [Fact]
    public void TryGet_WhenVersionDiffers_ReturnsFalse()
    {
        // Arrange
        var entry = TimedAvailabilityProbeCache.Create(version: 3, now: 10f, lifetimeSeconds: 0.25f, value: true);

        // Act
        var found = TimedAvailabilityProbeCache.TryGet(entry, version: 4, now: 10.1f, out var value);

        // Assert
        Assert.False(found);
        Assert.False(value);
    }

    [Fact]
    public void TryGet_WhenEntryExpired_ReturnsFalse()
    {
        // Arrange
        var entry = TimedAvailabilityProbeCache.Create(version: 3, now: 10f, lifetimeSeconds: 0.25f, value: true);

        // Act
        var found = TimedAvailabilityProbeCache.TryGet(entry, version: 3, now: 10.5f, out var value);

        // Assert
        Assert.False(found);
        Assert.False(value);
    }
}
