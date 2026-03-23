namespace SmartHauling.Runtime.Tests;

public sealed class GoalStallWatchdogTests
{
    [Fact]
    public void Evaluate_WhenObservedForTheFirstTime_RegistersProgress()
    {
        // Arrange
        GoalStallWatchdog.Signature? previousSignature = null;
        const float previousProgressAt = 0f;
        var currentSignature = CreateSignature(actionId: "GoToTarget", carryCount: 10, pickupQueueCount: 2);
        const float now = 15f;
        const float timeoutSeconds = 10f;

        // Act
        var result = GoalStallWatchdog.Evaluate(previousSignature, previousProgressAt, currentSignature, now, timeoutSeconds);

        // Assert
        Assert.True(result.HasProgressed);
        Assert.False(result.IsStalled);
        Assert.Equal(now, result.LastProgressAt);
        Assert.Equal(0f, result.StallDuration);
    }

    [Fact]
    public void Evaluate_WhenSignatureChanges_ResetsProgressTimer()
    {
        // Arrange
        var previousSignature = CreateSignature(actionId: "GoToTarget", carryCount: 10, pickupQueueCount: 2);
        const float previousProgressAt = 5f;
        var currentSignature = CreateSignature(actionId: "PickupResourceFromPile", carryCount: 15, pickupQueueCount: 1);
        const float now = 12f;
        const float timeoutSeconds = 10f;

        // Act
        var result = GoalStallWatchdog.Evaluate(previousSignature, previousProgressAt, currentSignature, now, timeoutSeconds);

        // Assert
        Assert.True(result.HasProgressed);
        Assert.False(result.IsStalled);
        Assert.Equal(now, result.LastProgressAt);
        Assert.Equal(0f, result.StallDuration);
    }

    [Fact]
    public void Evaluate_WhenSignatureStaysTheSameBeforeTimeout_Waits()
    {
        // Arrange
        var signature = CreateSignature(actionId: "GoToTarget", carryCount: 10, pickupQueueCount: 2);
        const float previousProgressAt = 5f;
        const float now = 11f;
        const float timeoutSeconds = 10f;

        // Act
        var result = GoalStallWatchdog.Evaluate(signature, previousProgressAt, signature, now, timeoutSeconds);

        // Assert
        Assert.False(result.HasProgressed);
        Assert.False(result.IsStalled);
        Assert.Equal(previousProgressAt, result.LastProgressAt);
        Assert.Equal(6f, result.StallDuration);
    }

    [Fact]
    public void Evaluate_WhenSignatureStaysTheSamePastTimeout_ReportsStall()
    {
        // Arrange
        var signature = CreateSignature(actionId: "GoToTarget", carryCount: 10, pickupQueueCount: 2);
        const float previousProgressAt = 5f;
        const float now = 16f;
        const float timeoutSeconds = 10f;

        // Act
        var result = GoalStallWatchdog.Evaluate(signature, previousProgressAt, signature, now, timeoutSeconds);

        // Assert
        Assert.False(result.HasProgressed);
        Assert.True(result.IsStalled);
        Assert.Equal(previousProgressAt, result.LastProgressAt);
        Assert.Equal(11f, result.StallDuration);
    }

    private static GoalStallWatchdog.Signature CreateSignature(string actionId, int carryCount, int pickupQueueCount)
    {
        return new GoalStallWatchdog.Signature(
            actionId: actionId,
            positionX: 1,
            positionY: 2,
            positionZ: 3,
            carryCount: carryCount,
            pickupQueueCount: pickupQueueCount,
            targetAIdentity: 101,
            targetBIdentity: 202,
            dropFailures: 0,
            dropPhaseLocked: false);
    }
}
