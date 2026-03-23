using NSMedieval.State;

namespace SmartHauling.Runtime;

internal static class StoragePriorityUtil
{
    public static ZonePriority GetEffectiveSourcePriority(ResourcePileInstance? pile)
    {
        if (pile == null || pile.HasDisposed)
        {
            return ZonePriority.None;
        }

        var storedResource = pile.GetStoredResource();
        if (storedResource != null &&
            !storedResource.HasDisposed &&
            pile.PlacedOnStorage != null &&
            pile.PlacedOnStorage.ResourcesFilter.IsValid(storedResource))
        {
            return pile.PlacedOnStorage.Priority;
        }

        return pile.StoragePriority;
    }
}
