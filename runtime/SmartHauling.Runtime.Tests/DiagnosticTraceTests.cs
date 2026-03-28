using SmartHauling.Runtime.Configuration;

namespace SmartHauling.Runtime.Tests;

public sealed class DiagnosticTraceTests : IDisposable
{
    public DiagnosticTraceTests()
    {
        DiagnosticTrace.Shutdown();
        DiagnosticTrace.Configure(DiagnosticLogLevel.Off);
    }

    public void Dispose()
    {
        DiagnosticTrace.Shutdown();
        DiagnosticTrace.Configure(DiagnosticLogLevel.Off);
    }

    [Fact]
    public void Raw_WhenTraceSessionIsActive_WritesToTraceFileAfterFlush()
    {
        // Arrange
        var traceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.trace.log");
        DiagnosticTrace.Configure(DiagnosticLogLevel.Trace);
        DiagnosticTrace.StartSession(traceFilePath);

        // Act
        DiagnosticTrace.Raw("test.trace", "first");
        DiagnosticTrace.Raw("test.trace", "second");

        // Assert
        Assert.True(DiagnosticTrace.FlushPending(), "Expected the async trace queue to flush within the timeout.");
        DiagnosticTrace.Shutdown();
        var traceText = File.ReadAllText(traceFilePath);
        Assert.Contains("[test.trace] first", traceText);
        Assert.Contains("[test.trace] second", traceText);
    }

    [Fact]
    public void Shutdown_WhenTraceLinesArePending_FlushesRemainingBatch()
    {
        // Arrange
        var traceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.trace.log");
        DiagnosticTrace.Configure(DiagnosticLogLevel.Trace);
        DiagnosticTrace.StartSession(traceFilePath);

        // Act
        for (var index = 0; index < 10; index++)
        {
            DiagnosticTrace.Raw("test.shutdown", $"line-{index}");
        }

        DiagnosticTrace.Shutdown();

        // Assert
        var traceText = File.ReadAllText(traceFilePath);
        Assert.Contains("[test.shutdown] line-0", traceText);
        Assert.Contains("[test.shutdown] line-9", traceText);
    }

    [Fact]
    public void StartSession_AfterShutdown_RestartsWriterForNewTraceFile()
    {
        var firstTraceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.first.trace.log");
        var secondTraceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.second.trace.log");
        DiagnosticTrace.Configure(DiagnosticLogLevel.Trace);

        DiagnosticTrace.StartSession(firstTraceFilePath);
        DiagnosticTrace.Raw("test.restart", "first-session");
        DiagnosticTrace.Shutdown();

        DiagnosticTrace.StartSession(secondTraceFilePath);
        DiagnosticTrace.Raw("test.restart", "second-session");
        Assert.True(DiagnosticTrace.FlushPending(), "Expected the restarted async trace queue to flush within the timeout.");
        DiagnosticTrace.Shutdown();

        Assert.Contains("[test.restart] first-session", File.ReadAllText(firstTraceFilePath));
        Assert.Contains("[test.restart] second-session", File.ReadAllText(secondTraceFilePath));
    }
}
