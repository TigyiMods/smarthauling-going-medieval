using BepInEx.Configuration;

namespace SmartHauling.Runtime.Configuration;

/// <summary>
/// Exposes user-configurable runtime settings through the standard BepInEx config file.
/// </summary>
internal static class SmartHaulingSettings
{
    private static ConfigEntry<bool>? enableDiagnosticTrace;
    private static ConfigEntry<DiagnosticLogLevel>? diagnosticTraceLevel;
    private static ConfigEntry<bool>? enableStallWatchdog;
    private static ConfigEntry<float>? stallWatchdogTimeoutSeconds;

    public static DiagnosticLogLevel DiagnosticTraceLevel =>
        ResolveDiagnosticTraceLevel(
            enableDiagnosticTrace?.Value ?? true,
            diagnosticTraceLevel?.Value ?? DiagnosticLogLevel.Trace);

    public static bool EnableStallWatchdog => enableStallWatchdog?.Value ?? true;

    public static float StallWatchdogTimeoutSeconds =>
        ResolveStallWatchdogTimeoutSeconds(stallWatchdogTimeoutSeconds?.Value ?? 10f);

    public static void Initialize(ConfigFile config)
    {
        enableDiagnosticTrace = config.Bind(
            "Tracing",
            "EnableDiagnosticTrace",
            true,
            "Legacy master switch for diagnostic tracing. If false, tracing is fully disabled regardless of DiagnosticTraceLevel.");

        diagnosticTraceLevel = config.Bind(
            "Tracing",
            "DiagnosticTraceLevel",
            DiagnosticLogLevel.Trace,
            "Minimum diagnostic log level. Valid values: Off, Error, Info, Trace.");

        enableStallWatchdog = config.Bind(
            "Behaviour",
            "EnableStallWatchdog",
            true,
            "Ends stalled stockpile hauling and smart unload goals if they stop making progress for too long.");

        stallWatchdogTimeoutSeconds = config.Bind(
            "Behaviour",
            "StallWatchdogTimeoutSeconds",
            10f,
            "Seconds without meaningful progress before the hauling stall watchdog aborts the goal.");
    }

    internal static DiagnosticLogLevel ResolveDiagnosticTraceLevel(
        bool enableDiagnosticTrace,
        DiagnosticLogLevel configuredLevel)
    {
        return enableDiagnosticTrace
            ? configuredLevel
            : DiagnosticLogLevel.Off;
    }

    internal static float ResolveStallWatchdogTimeoutSeconds(float configuredTimeoutSeconds)
    {
        return configuredTimeoutSeconds < 4f
            ? 4f
            : configuredTimeoutSeconds;
    }
}
