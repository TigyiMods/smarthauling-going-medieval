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
        // Arrange
        var firstTraceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.first.trace.log");
        var secondTraceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.second.trace.log");
        DiagnosticTrace.Configure(DiagnosticLogLevel.Trace);

        // Act
        DiagnosticTrace.StartSession(firstTraceFilePath);
        DiagnosticTrace.Raw("test.restart", "first-session");
        DiagnosticTrace.Shutdown();

        DiagnosticTrace.StartSession(secondTraceFilePath);
        DiagnosticTrace.Raw("test.restart", "second-session");
        Assert.True(DiagnosticTrace.FlushPending(), "Expected the restarted async trace queue to flush within the timeout.");
        DiagnosticTrace.Shutdown();

        // Assert
        Assert.Contains("[test.restart] first-session", File.ReadAllText(firstTraceFilePath));
        Assert.Contains("[test.restart] second-session", File.ReadAllText(secondTraceFilePath));
    }

    [Fact]
    public void Raw_Factory_WhenTraceIsDisabled_DoesNotEvaluateMessage()
    {
        // Arrange
        var invoked = false;

        // Act
        DiagnosticTrace.Raw("test.lazy", () =>
        {
            invoked = true;
            return "should-not-log";
        });

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void Info_Factory_WhenCategoryLimitIsExhausted_DoesNotEvaluateMessage()
    {
        // Arrange
        DiagnosticTrace.Configure(DiagnosticLogLevel.Info);
        DiagnosticTrace.Info("test.limit", "first", 1);
        var invoked = false;

        // Act
        DiagnosticTrace.Info("test.limit", () =>
        {
            invoked = true;
            return "second";
        }, 1);

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void EnsureSessionStarted_AfterShutdown_RestartsWriterWithoutNewPluginAwake()
    {
        // Arrange
        var traceFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.reactivated.trace.log");
        DiagnosticTrace.Configure(DiagnosticLogLevel.Trace);
        DiagnosticTrace.StartSession(traceFilePath);
        DiagnosticTrace.Raw("test.reactivate", "before-shutdown");
        Assert.True(DiagnosticTrace.FlushPending(), "Expected the initial trace queue to flush within the timeout.");
        DiagnosticTrace.Shutdown();

        // Act
        DiagnosticTrace.EnsureSessionStarted(traceFilePath);
        DiagnosticTrace.Raw("test.reactivate", "after-shutdown");

        // Assert
        Assert.True(DiagnosticTrace.FlushPending(), "Expected the reactivated trace queue to flush within the timeout.");
        DiagnosticTrace.Shutdown();
        var traceText = File.ReadAllText(traceFilePath);
        Assert.Contains("[test.reactivate] before-shutdown", traceText);
        Assert.Contains("[test.reactivate] after-shutdown", traceText);
    }
}
