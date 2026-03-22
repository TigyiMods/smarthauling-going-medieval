namespace SmartHauling.Runtime;

internal static class RuntimeActivation
{
    private static bool isActive;

    public static bool IsActive => isActive;

    public static void Activate(string reason)
    {
        if (isActive)
        {
            return;
        }

        isActive = true;
        DiagnosticTrace.Raw("activation", $"Gameplay patches activated: {reason}");
    }

    public static void Deactivate(string reason)
    {
        if (!isActive)
        {
            return;
        }

        isActive = false;
        DiagnosticTrace.Raw("activation", $"Gameplay patches deactivated: {reason}");
    }
}
