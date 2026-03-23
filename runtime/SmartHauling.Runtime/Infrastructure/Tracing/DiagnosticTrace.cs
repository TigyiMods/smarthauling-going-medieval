using System.Text;
using BepInEx;
using SmartHauling.Runtime.Configuration;

namespace SmartHauling.Runtime;

internal static class DiagnosticTrace
{
    private static readonly System.Collections.Generic.Dictionary<string, int> RemainingByCategory = new();
    private static readonly object CounterSyncRoot = new();
    private static readonly object FileSyncRoot = new();

    private static DiagnosticLogLevel currentLevel = DiagnosticLogLevel.Trace;
    private static string? traceFilePath;

    public static string? TraceFilePath => traceFilePath;
    public static bool IsEnabled => currentLevel != DiagnosticLogLevel.Off;
    public static DiagnosticLogLevel CurrentLevel => currentLevel;

    public static void Configure(DiagnosticLogLevel level)
    {
        currentLevel = level;

        lock (CounterSyncRoot)
        {
            RemainingByCategory.Clear();
        }

        if (!IsEnabled)
        {
            traceFilePath = null;
        }
    }

    public static void StartSession()
    {
        if (!IsEnabled)
        {
            traceFilePath = null;
            return;
        }

        traceFilePath = Path.Combine(Paths.BepInExRootPath, "SmartHauling.trace.log");
        if (ShouldLogLevel(DiagnosticLogLevel.Info))
        {
            WriteLine(DiagnosticLogLevel.Info, "session", $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
        }
    }

    public static void Info(string category, string message, int limit = 20)
    {
        if (!ShouldLog(DiagnosticLogLevel.Info, category, limit))
        {
            return;
        }

        WriteLine(DiagnosticLogLevel.Info, category, message);
    }

    public static void Error(string category, string message)
    {
        if (!ShouldLogLevel(DiagnosticLogLevel.Error))
        {
            return;
        }

        WriteLine(DiagnosticLogLevel.Error, category, message);
    }

    public static void Raw(string category, string message)
    {
        if (!ShouldLogLevel(DiagnosticLogLevel.Trace))
        {
            return;
        }

        WriteLine(DiagnosticLogLevel.Trace, category, message);
    }

    private static bool ShouldLog(DiagnosticLogLevel level, string category, int defaultLimit)
    {
        if (!ShouldLogLevel(level))
        {
            return false;
        }

        lock (CounterSyncRoot)
        {
            if (!RemainingByCategory.TryGetValue(category, out var remaining))
            {
                remaining = defaultLimit;
            }

            if (remaining <= 0)
            {
                return false;
            }

            RemainingByCategory[category] = remaining - 1;
            return true;
        }
    }

    private static bool ShouldLogLevel(DiagnosticLogLevel level)
    {
        return currentLevel >= level && currentLevel != DiagnosticLogLevel.Off;
    }

    private static void WriteLine(DiagnosticLogLevel level, string category, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}";

        try
        {
            switch (level)
            {
                case DiagnosticLogLevel.Error:
                    SmartHaulingPlugin.Logger?.LogError(line);
                    break;
                case DiagnosticLogLevel.Trace:
                    SmartHaulingPlugin.Logger?.LogDebug(line);
                    break;
                default:
                    SmartHaulingPlugin.Logger?.LogInfo(line);
                    break;
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(traceFilePath))
        {
            return;
        }

        try
        {
            lock (FileSyncRoot)
            {
                File.AppendAllText(traceFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
