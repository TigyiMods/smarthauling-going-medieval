using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SmartHauling.Runtime.Composition;
using SmartHauling.Runtime.Configuration;
using UnityEngine;

namespace SmartHauling.Runtime;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInProcess("Going Medieval.exe")]
public sealed class SmartHaulingPlugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger { get; private set; } = null!;

    private Harmony? harmony;

    private void Awake()
    {
        Logger = base.Logger;
        SmartHaulingSettings.Initialize(Config);
        RuntimeServices.InitializeDefaults();
        DiagnosticTrace.Configure(SmartHaulingSettings.DiagnosticTraceLevel);
        DiagnosticTrace.StartSession();
        SmartHaulingLocalization.EnsureRegistered();
        harmony = new Harmony(PluginInfo.Guid);
        Logger.LogInfo($"{PluginInfo.Name} initializing on Unity {Application.unityVersion}.");
        Logger.LogInfo($"Diagnostic trace level: {DiagnosticTrace.CurrentLevel}");
        DiagnosticTrace.Raw("bootstrap", $"Awake entered. Trace file: {DiagnosticTrace.TraceFilePath}");

        try
        {
            harmony.PatchAll();
            var patchedMethods = Harmony.GetAllPatchedMethods().ToList();
            Logger.LogInfo($"{PluginInfo.Name} loaded.");
            Logger.LogInfo($"Patched methods: {patchedMethods.Count}");
            DiagnosticTrace.Raw("bootstrap", $"PatchAll completed. Patched methods: {patchedMethods.Count}");
            foreach (var method in patchedMethods.Take(40))
            {
                DiagnosticTrace.Raw("patched", $"{method.DeclaringType?.FullName}.{method.Name}");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Failed to apply Harmony patches: {ex}");
            DiagnosticTrace.Error("bootstrap.error", ex.ToString());
            throw;
        }
    }

    private void OnDestroy()
    {
        DiagnosticTrace.Raw("bootstrap", "Plugin OnDestroy invoked. Harmony remains patched; runtime activation is patch-driven.");
    }
}
