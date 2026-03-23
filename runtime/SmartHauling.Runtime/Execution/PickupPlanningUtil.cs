using NSMedieval.Components;
using NSMedieval.Model;
using NSMedieval.Types;
using UnityEngine;

namespace SmartHauling.Runtime;

internal static class PickupPlanningUtil
{
    public static int GetProjectedCapacity(Storage storage, Resource resource, float plannedWeight, bool plannedAny)
    {
        if (resource == null)
        {
            return 0;
        }

        var freeSpace = storage.GetFreeSpace() - plannedWeight;
        var hasContents = plannedAny || storage.HasOneOrMoreResources();
        if (resource.EquipmentBlueprint != null)
        {
            return freeSpace >= resource.Weight ? 1 : 0;
        }

        if ((resource.Category & (ResourceCategory.CtgCarcass | ResourceCategory.CtgStructure)) != ResourceCategory.None)
        {
            return hasContents ? 0 : 1;
        }

        if (resource.Category == ResourceCategory.None && resource.GetID().Contains("trophy"))
        {
            return hasContents ? 0 : 1;
        }

        if (storage.StorageBase.IgnoreWeigth)
        {
            return Mathf.Max(0, (int)freeSpace);
        }

        if ((float)(int)freeSpace < resource.Weight)
        {
            return hasContents ? 0 : 1;
        }

        return Mathf.Max(0, (int)(freeSpace / resource.Weight));
    }

    public static float GetProjectedWeight(Storage storage, Resource resource, int amount)
    {
        return amount * (storage.StorageBase.IgnoreWeigth ? 1f : resource.Weight);
    }
}
