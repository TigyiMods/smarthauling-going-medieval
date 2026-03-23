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
}
