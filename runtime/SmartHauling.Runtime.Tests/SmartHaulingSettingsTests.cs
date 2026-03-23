using SmartHauling.Runtime.Configuration;

namespace SmartHauling.Runtime.Tests;

public sealed class SmartHaulingSettingsTests
{
    [Fact]
    public void ResolveDiagnosticTraceLevel_WhenLegacySwitchIsDisabled_ReturnsOff()
    {
        // Arrange
        const DiagnosticLogLevel configuredLevel = DiagnosticLogLevel.Trace;

        // Act
        var result = SmartHaulingSettings.ResolveDiagnosticTraceLevel(false, configuredLevel);

        // Assert
        Assert.Equal(DiagnosticLogLevel.Off, result);
    }

    [Fact]
    public void ResolveDiagnosticTraceLevel_WhenLegacySwitchIsEnabled_ReturnsConfiguredLevel()
    {
        // Arrange
        const DiagnosticLogLevel configuredLevel = DiagnosticLogLevel.Error;

        // Act
        var result = SmartHaulingSettings.ResolveDiagnosticTraceLevel(true, configuredLevel);

        // Assert
        Assert.Equal(configuredLevel, result);
    }

    [Fact]
    public void ResolveStallWatchdogTimeoutSeconds_WhenConfiguredBelowMinimum_ClampsToMinimum()
    {
        // Arrange
        const float configuredTimeoutSeconds = 2f;

        // Act
        var result = SmartHaulingSettings.ResolveStallWatchdogTimeoutSeconds(configuredTimeoutSeconds);

        // Assert
        Assert.Equal(4f, result);
    }

    [Fact]
    public void ResolveStallWatchdogTimeoutSeconds_WhenConfiguredAboveMinimum_ReturnsConfiguredValue()
    {
        // Arrange
        const float configuredTimeoutSeconds = 9.5f;

        // Act
        var result = SmartHaulingSettings.ResolveStallWatchdogTimeoutSeconds(configuredTimeoutSeconds);

        // Assert
        Assert.Equal(configuredTimeoutSeconds, result);
    }
}
