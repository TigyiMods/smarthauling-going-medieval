namespace SmartHauling.Runtime.Tests;

public sealed class ReferenceEqualityComparerTests
{
    [Fact]
    public void Equals_WhenBothArgumentsReferenceSameFake_ReturnsTrue()
    {
        // Arrange
        var instance = A.Fake<IDisposable>();

        // Act
        var result = ReferenceEqualityComparer<IDisposable>.Instance.Equals(instance, instance);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Equals_WhenArgumentsReferenceDifferentFakes_ReturnsFalse()
    {
        // Arrange
        var first = A.Fake<IDisposable>();
        var second = A.Fake<IDisposable>();

        // Act
        var result = ReferenceEqualityComparer<IDisposable>.Instance.Equals(first, second);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetHashCode_WhenCalledForSameFakeMultipleTimes_ReturnsStableValue()
    {
        // Arrange
        var instance = A.Fake<IDisposable>();

        // Act
        var firstHashCode = ReferenceEqualityComparer<IDisposable>.Instance.GetHashCode(instance);
        var secondHashCode = ReferenceEqualityComparer<IDisposable>.Instance.GetHashCode(instance);

        // Assert
        Assert.Equal(firstHashCode, secondHashCode);
    }
}
