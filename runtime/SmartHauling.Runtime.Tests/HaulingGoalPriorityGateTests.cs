using NSMedieval.State.WorkerJobs;

namespace SmartHauling.Runtime.Tests;

public sealed class HaulingGoalPriorityGateTests
{
    [Fact]
    public void TryAllowForcedHauling_ReturnsTrue_WhenHaulingIsUniqueHighestPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.True(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenHaulingIsNotHighestPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 2f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.False(allowed);
        Assert.Null(blockingJob);
        Assert.Equal(float.MaxValue, blockingPriority);
    }

    [Fact]
    public void TryAllowForcedHauling_ReturnsFalse_WhenAnotherJobSharesHighestPriority()
    {
        // Arrange
        var priorities = BuildPriorities(defaultPriority: 3f);
        priorities[JobType.Hauling] = 1f;
        priorities[JobType.Construction] = 1f;

        // Act
        var allowed = HaulingGoalPriorityGate.TryAllowForcedHauling(
            job => priorities[job],
            out var blockingJob,
            out var blockingPriority);

        // Assert
        Assert.False(allowed);
        Assert.Equal(JobType.Construction, blockingJob);
        Assert.Equal(1f, blockingPriority);
    }

    private static Dictionary<JobType, float> BuildPriorities(float defaultPriority)
    {
        var priorities = new Dictionary<JobType, float>();
        foreach (JobType job in Enum.GetValues(typeof(JobType)))
        {
            priorities[job] = defaultPriority;
        }

        return priorities;
    }
}
