using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BepInEx;
using SmartHauling.Runtime.Configuration;

namespace SmartHauling.Runtime;

internal static class DiagnosticTrace
{
    private static class WriterPolicy
    {
        public static readonly TimeSpan IdleFlushInterval = TimeSpan.FromMilliseconds(100);
        public const int MaxBatchSize = 128;
        public const int FlushPendingTimeoutMs = 2000;
        public const int FlushPollIntervalMs = 10;
        public const int ShutdownJoinTimeoutMs = 2000;
    }

    private static readonly System.Collections.Generic.Dictionary<string, int> RemainingByCategory = new();
    private static readonly object CounterSyncRoot = new();
    private static readonly object WriterSyncRoot = new();
    private static readonly ConcurrentQueue<string> PendingFileLines = new();
    private static readonly AutoResetEvent PendingFileSignal = new(false);

    private static DiagnosticLogLevel currentLevel = DiagnosticLogLevel.Trace;
    private static string? traceFilePath;
    private static Thread? writerThread;
    private static int pendingFileLineCount;
    private static volatile bool writerStopRequested;

    public static string? TraceFilePath => traceFilePath;
    public static bool IsEnabled => currentLevel != DiagnosticLogLevel.Off;
    public static DiagnosticLogLevel CurrentLevel => currentLevel;

    public static void Configure(DiagnosticLogLevel level)
    {
        StopWriter();
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

    public static void StartSession(string? overrideTraceFilePath = null)
    {
        if (!IsEnabled)
        {
            traceFilePath = null;
            return;
        }

        StopWriter();
        traceFilePath = overrideTraceFilePath ?? Path.Combine(Paths.BepInExRootPath, "SmartHauling.trace.log");
        StartWriter();
        if (ShouldLogLevel(DiagnosticLogLevel.Info))
        {
            WriteLine(DiagnosticLogLevel.Info, "session", $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
        }
    }

    public static void Shutdown()
    {
        StopWriter();
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

        WriteToLogger(level, line);

        if (string.IsNullOrWhiteSpace(traceFilePath))
        {
            return;
        }

        PendingFileLines.Enqueue(line);
        Interlocked.Increment(ref pendingFileLineCount);
        PendingFileSignal.Set();
    }

    internal static bool FlushPending(int timeoutMs = WriterPolicy.FlushPendingTimeoutMs)
    {
        if (Volatile.Read(ref pendingFileLineCount) <= 0)
        {
            return true;
        }

        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref pendingFileLineCount) > 0 && deadline.ElapsedMilliseconds < timeoutMs)
        {
            PendingFileSignal.Set();
            Thread.Sleep(WriterPolicy.FlushPollIntervalMs);
        }

        return Volatile.Read(ref pendingFileLineCount) == 0;
    }

    private static void WriteToLogger(DiagnosticLogLevel level, string line)
    {
        // Keep user-facing info/error diagnostics immediate, but route trace spam to the
        // dedicated trace file so the main game loop does not synchronously spam LogOutput.
        if (level == DiagnosticLogLevel.Trace)
        {
            return;
        }

        try
        {
            switch (level)
            {
                case DiagnosticLogLevel.Error:
                    SmartHaulingPlugin.Logger?.LogError(line);
                    break;
                default:
                    SmartHaulingPlugin.Logger?.LogInfo(line);
                    break;
            }
        }
        catch
        {
        }
    }

    private static void StartWriter()
    {
        lock (WriterSyncRoot)
        {
            if (string.IsNullOrWhiteSpace(traceFilePath) || writerThread != null)
            {
                return;
            }

            writerStopRequested = false;
            writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SmartHaulingTraceWriter"
            };
            writerThread.Start();
        }
    }

    private static void StopWriter()
    {
        Thread? threadToJoin;
        lock (WriterSyncRoot)
        {
            threadToJoin = writerThread;
            if (threadToJoin == null)
            {
                return;
            }

            writerStopRequested = true;
            PendingFileSignal.Set();
        }

        try
        {
            threadToJoin.Join(WriterPolicy.ShutdownJoinTimeoutMs);
        }
        catch
        {
        }
    }

    private static void WriterLoop()
    {
        if (string.IsNullOrWhiteSpace(traceFilePath))
        {
            DrainPendingWithoutWriting();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(traceFilePath)!);

            using var stream = new FileStream(traceFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            var batch = new List<string>(WriterPolicy.MaxBatchSize);

            while (true)
            {
                DrainBatch(batch);

                if (batch.Count > 0)
                {
                    foreach (var line in batch)
                    {
                        writer.WriteLine(line);
                    }

                    writer.Flush();
                    batch.Clear();
                }

                if (writerStopRequested && PendingFileLines.IsEmpty)
                {
                    break;
                }

                PendingFileSignal.WaitOne(WriterPolicy.IdleFlushInterval);
            }
        }
        catch
        {
            DrainPendingWithoutWriting();
        }
        finally
        {
            lock (WriterSyncRoot)
            {
                if (ReferenceEquals(writerThread, Thread.CurrentThread))
                {
                    writerThread = null;
                    writerStopRequested = false;
                }
            }
        }
    }

    private static void DrainBatch(List<string> batch)
    {
        while (batch.Count < WriterPolicy.MaxBatchSize && PendingFileLines.TryDequeue(out var line))
        {
            batch.Add(line);
            Interlocked.Decrement(ref pendingFileLineCount);
        }
    }

    private static void DrainPendingWithoutWriting()
    {
        while (PendingFileLines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingFileLineCount);
        }
    }
}
