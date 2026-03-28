using I2.Loc;
using HarmonyLib;

namespace SmartHauling.Runtime;

internal static class SmartHaulingLocalization
{
    internal const string VanillaStockpileHaulingActionTerm = "worker_action_stockpilehaulinggoal";
    internal const string VanillaStockpileHaulingGoalTerm = "StockpileHaulingGoal";
    internal const string SmartSuffixTerm = "Mods/SmartHauling/StatusSuffix";
    internal const string StockpileHaulingGoalNameTerm = "Mods/SmartHauling/GoalName/StockpileHauling";
    internal const string SmartUnloadGoalNameTerm = "Mods/SmartHauling/GoalName/SmartUnload";

    internal const string DefaultSmartSuffix = "(smart)";
    internal const string DefaultStockpileHaulingGoalName = "Hauling";
    internal const string DefaultSmartUnloadGoalName = "Unloading";

    private static readonly System.Reflection.MethodInfo AddSourceMethod =
        AccessTools.Method(typeof(LocalizationManager), "AddSource", new[] { typeof(LanguageSourceData) })!;

    private static bool isRegistered;

    public static void EnsureRegistered()
    {
        if (isRegistered)
        {
            return;
        }

        try
        {
            LocalizationManager.InitializeIfNeeded();

            if (LocalizationManager.GetSourceContaining(SmartSuffixTerm, false) != null)
            {
                isRegistered = true;
                return;
            }

            var source = new LanguageSourceData();
            foreach (var languageCode in LocalizationManager.GetAllLanguagesCode(true, false).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var languageName = LocalizationManager.GetLanguageFromCode(languageCode, false);
                source.AddLanguage(
                    string.IsNullOrWhiteSpace(languageName) ? languageCode : languageName,
                    languageCode);
            }

            var languages = source.GetLanguages(false);
            RegisterTerm(source, languages, VanillaStockpileHaulingActionTerm, DefaultStockpileHaulingGoalName);
            RegisterTerm(source, languages, VanillaStockpileHaulingGoalTerm, DefaultStockpileHaulingGoalName);
            RegisterTerm(source, languages, SmartSuffixTerm, DefaultSmartSuffix);
            RegisterTerm(source, languages, StockpileHaulingGoalNameTerm, DefaultStockpileHaulingGoalName);
            RegisterTerm(source, languages, SmartUnloadGoalNameTerm, DefaultSmartUnloadGoalName);

            source.UpdateDictionary(true);
            AddSourceMethod.Invoke(null, new object[] { source });
            isRegistered = true;
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Error("loc.error", $"Failed to register SmartHauling localization source: {ex}");
        }
    }

    private static void RegisterTerm(LanguageSourceData source, List<string> languages, string termName, string defaultValue)
    {
        var term = source.AddTerm(termName);
        if (term == null)
        {
            return;
        }

        for (var index = 0; index < languages.Count; index++)
        {
            term.SetTranslation(index, defaultValue, null);
        }
    }
}
