using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;
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
        DiagnosticTrace.StartSession();
        harmony = new Harmony(PluginInfo.Guid);
        Logger.LogInfo($"{PluginInfo.Name} initializing on Unity {Application.unityVersion}.");
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
                Logger.LogInfo($"Patched -> {method.DeclaringType?.FullName}.{method.Name}");
                DiagnosticTrace.Raw("patched", $"{method.DeclaringType?.FullName}.{method.Name}");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Failed to apply Harmony patches: {ex}");
            DiagnosticTrace.Raw("bootstrap.error", ex.ToString());
            throw;
        }
    }

    private void OnDestroy()
    {
        DiagnosticTrace.Raw("bootstrap", "Plugin OnDestroy invoked. Harmony remains patched; runtime activation is patch-driven.");
    }
}
