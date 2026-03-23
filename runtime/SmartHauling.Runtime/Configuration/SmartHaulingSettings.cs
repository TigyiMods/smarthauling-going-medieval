using BepInEx.Configuration;

namespace SmartHauling.Runtime.Configuration;

/// <summary>
/// Exposes user-configurable runtime settings through the standard BepInEx config file.
/// </summary>
internal static class SmartHaulingSettings
{
    private static ConfigEntry<bool>? enableDiagnosticTrace;
    private static ConfigEntry<DiagnosticLogLevel>? diagnosticTraceLevel;

    public static DiagnosticLogLevel DiagnosticTraceLevel =>
        ResolveDiagnosticTraceLevel(
            enableDiagnosticTrace?.Value ?? true,
            diagnosticTraceLevel?.Value ?? DiagnosticLogLevel.Trace);

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
    }

    internal static DiagnosticLogLevel ResolveDiagnosticTraceLevel(
        bool enableDiagnosticTrace,
        DiagnosticLogLevel configuredLevel)
    {
        return enableDiagnosticTrace
            ? configuredLevel
            : DiagnosticLogLevel.Off;
    }
}
