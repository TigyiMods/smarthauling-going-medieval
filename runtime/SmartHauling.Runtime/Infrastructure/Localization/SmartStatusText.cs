using I2.Loc;

namespace SmartHauling.Runtime;

internal static class SmartStatusText
{
    private const string DefaultSmartSuffix = " (smart)";

    private static readonly Dictionary<string, string> CachedSuffixByLanguageCode = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolveDisplayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        try
        {
            if (LocalizationManager.TryGetTranslation(
                    text,
                    out var translatedText,
                    false,
                    0,
                    false,
                    false,
                    null,
                    null,
                    false) &&
                IsUsableTranslation(translatedText))
            {
                return translatedText;
            }
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Error("loc.error", $"ResolveDisplayText failed for '{text}': {ex.GetType().Name}: {ex.Message}");
        }

        return text;
    }

    public static string NormalizeGoalDisplayText(string text, string fallbackTerm, string fallbackText)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var resolved = ResolveDisplayText(text);
        if (LooksLikePlaceholderKey(resolved))
        {
            return ResolveLocalizedFallback(fallbackTerm, fallbackText);
        }

        return resolved;
    }

    public static string AppendSmartSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return AppendSuffix(text, GetSmartSuffix());
    }

    public static string ResolveLocalizedFallback(string termName, string fallbackText)
    {
        try
        {
            if (LocalizationManager.TryGetTranslation(
                    termName,
                    out var translatedText,
                    false,
                    0,
                    false,
                    false,
                    null,
                    null,
                    false) &&
                IsUsableTranslation(translatedText))
            {
                return translatedText;
            }
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Error("loc.error", $"ResolveLocalizedFallback failed for '{termName}': {ex.GetType().Name}: {ex.Message}");
        }

        return fallbackText;
    }

    internal static string AppendSuffix(string text, string suffix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var normalizedSuffix = NormalizeSuffix(suffix);
        return text.EndsWith(normalizedSuffix, StringComparison.Ordinal)
            ? text
            : text + normalizedSuffix;
    }

    private static string GetSmartSuffix()
    {
        var languageCode = TryGetCurrentLanguageCode();
        if (CachedSuffixByLanguageCode.TryGetValue(languageCode, out var cachedSuffix))
        {
            return cachedSuffix;
        }

        var resolvedSuffix = ResolveSmartSuffix();
        CachedSuffixByLanguageCode[languageCode] = resolvedSuffix;
        return resolvedSuffix;
    }

    private static string ResolveSmartSuffix()
    {
        try
        {
            if (LocalizationManager.TryGetTranslation(
                    SmartHaulingLocalization.SmartSuffixTerm,
                    out var translatedSuffix,
                    false,
                    0,
                    false,
                    false,
                    null,
                    null,
                    false) &&
                IsUsableTranslation(translatedSuffix))
            {
                return NormalizeSuffix(translatedSuffix);
            }
        }
        catch
        {
        }

        return DefaultSmartSuffix;
    }

    internal static bool IsUsableTranslation(string? translatedSuffix)
    {
        if (string.IsNullOrWhiteSpace(translatedSuffix))
        {
            return false;
        }

        var trimmed = translatedSuffix.Trim();
        if (string.Equals(trimmed, SmartHaulingLocalization.SmartSuffixTerm, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.IndexOf(SmartHaulingLocalization.SmartSuffixTerm, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (trimmed.IndexOf("Mods/SmartHauling/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return !(trimmed.StartsWith("[", StringComparison.Ordinal) &&
                 trimmed.EndsWith("]", StringComparison.Ordinal));
    }

    internal static bool LooksLikePlaceholderKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        return trimmed.StartsWith("worker_action_", StringComparison.OrdinalIgnoreCase) ||
               (trimmed.IndexOf('_') >= 0 && trimmed.IndexOf(' ') < 0);
    }

    private static string TryGetCurrentLanguageCode()
    {
        try
        {
            return LocalizationManager.CurrentLanguageCode ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return DefaultSmartSuffix;
        }

        return suffix.StartsWith(' ') ? suffix : " " + suffix;
    }
}
