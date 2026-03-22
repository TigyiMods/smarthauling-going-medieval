using System;
using System.IO;
using System.Text;
using BepInEx;

namespace SmartHauling.Runtime;

internal static class DiagnosticTrace
{
    private static readonly System.Collections.Generic.Dictionary<string, int> RemainingByCategory = new();
    private static readonly object CounterSyncRoot = new();
    private static readonly object FileSyncRoot = new();

    private static string? traceFilePath;

    public static string? TraceFilePath => traceFilePath;

    public static void StartSession()
    {
        traceFilePath = Path.Combine(Paths.BepInExRootPath, "SmartHauling.trace.log");
        WriteLine("session", $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
    }

    public static void Info(string category, string message, int limit = 20)
    {
        if (!ShouldLog(category, limit))
        {
            return;
        }

        WriteLine(category, message);
    }

    public static void Raw(string category, string message)
    {
        WriteLine(category, message);
    }

    private static bool ShouldLog(string category, int defaultLimit)
    {
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

    private static void WriteLine(string category, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}";

        try
        {
            SmartHaulingPlugin.Logger?.LogInfo(line);
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
