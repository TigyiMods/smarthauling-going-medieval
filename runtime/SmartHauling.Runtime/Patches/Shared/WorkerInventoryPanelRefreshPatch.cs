using System.Runtime.CompilerServices;
using HarmonyLib;
using NSMedieval.State;
using NSMedieval.UI;

namespace SmartHauling.Runtime.Patches;

[HarmonyPatch]
internal static class WorkerInventoryPanelRefreshPatch
{
    private static readonly ConditionalWeakTable<WorkerInventoryExtraPanel, PanelHandlers> AttachedPanels = new();

    private static readonly Func<SelectionExtraPanelBase, HumanoidInstance?> HumanoidGetter =
        (Func<SelectionExtraPanelBase, HumanoidInstance?>)Delegate.CreateDelegate(
            typeof(Func<SelectionExtraPanelBase, HumanoidInstance?>),
            AccessTools.PropertyGetter(typeof(SelectionExtraPanelBase), "Humanoid")!);

    private static readonly System.Reflection.MethodInfo StorageUpdatedMethod =
        AccessTools.Method(typeof(WorkerInventoryExtraPanel), "StorageUpdated", new[] { typeof(SimpleResourceCount) })!;

    private static readonly System.Reflection.MethodInfo FoodStorageUpdatedMethod =
        AccessTools.Method(typeof(WorkerInventoryExtraPanel), "OnFoodStorageUpdated", new[] { typeof(SimpleResourceCount) })!;

    private static readonly System.Reflection.MethodInfo MedicineStorageUpdatedMethod =
        AccessTools.Method(typeof(WorkerInventoryExtraPanel), "OnMedicineStorageUpdated", new[] { typeof(SimpleResourceCount) })!;

    [HarmonyPatch(typeof(WorkerInventoryExtraPanel), "SetupTabPanel")]
    [HarmonyPostfix]
    private static void SetupTabPanelPostfix(WorkerInventoryExtraPanel __instance)
    {
        if (!RuntimeActivation.IsActive)
        {
            return;
        }

        AttachDeletedResourceHandlers(__instance);
    }

    [HarmonyPatch(typeof(WorkerInventoryExtraPanel), nameof(WorkerInventoryExtraPanel.Hide))]
    [HarmonyPrefix]
    private static void HidePrefix(WorkerInventoryExtraPanel __instance)
    {
        DetachDeletedResourceHandlers(__instance);
    }

    private static void AttachDeletedResourceHandlers(WorkerInventoryExtraPanel panel)
    {
        DetachDeletedResourceHandlers(panel);

        var humanoid = HumanoidGetter(panel);
        if (humanoid?.Storage == null || humanoid.FoodStorage == null || humanoid.MedicineStorage == null)
        {
            return;
        }

        var handlers = new PanelHandlers(
            humanoid,
            CreateResourceDeletedHandler(panel, StorageUpdatedMethod),
            CreateResourceDeletedHandler(panel, FoodStorageUpdatedMethod),
            CreateResourceDeletedHandler(panel, MedicineStorageUpdatedMethod));

        humanoid.Storage.ResourceDeletedEvent += handlers.OnStorageDeleted;
        humanoid.FoodStorage.ResourceDeletedEvent += handlers.OnFoodStorageDeleted;
        humanoid.MedicineStorage.ResourceDeletedEvent += handlers.OnMedicineStorageDeleted;
        AttachedPanels.Add(panel, handlers);
    }

    private static void DetachDeletedResourceHandlers(WorkerInventoryExtraPanel panel)
    {
        if (!AttachedPanels.TryGetValue(panel, out var handlers))
        {
            return;
        }

        if (handlers.Humanoid.Storage != null)
        {
            handlers.Humanoid.Storage.ResourceDeletedEvent -= handlers.OnStorageDeleted;
        }

        if (handlers.Humanoid.FoodStorage != null)
        {
            handlers.Humanoid.FoodStorage.ResourceDeletedEvent -= handlers.OnFoodStorageDeleted;
        }

        if (handlers.Humanoid.MedicineStorage != null)
        {
            handlers.Humanoid.MedicineStorage.ResourceDeletedEvent -= handlers.OnMedicineStorageDeleted;
        }

        AttachedPanels.Remove(panel);
    }

    private static Action<ResourceInstance> CreateResourceDeletedHandler(
        WorkerInventoryExtraPanel panel,
        System.Reflection.MethodInfo refreshMethod)
    {
        return resourceInstance =>
        {
            if (panel == null || !RuntimeActivation.IsActive)
            {
                return;
            }

            refreshMethod.Invoke(panel, new object[] { ToSimpleResourceCount(resourceInstance) });
        };
    }

    private static SimpleResourceCount ToSimpleResourceCount(ResourceInstance resourceInstance)
    {
        return resourceInstance != null && !resourceInstance.HasDisposed
            ? new SimpleResourceCount(resourceInstance)
            : default;
    }

    private sealed class PanelHandlers
    {
        public PanelHandlers(
            HumanoidInstance humanoid,
            Action<ResourceInstance> onStorageDeleted,
            Action<ResourceInstance> onFoodStorageDeleted,
            Action<ResourceInstance> onMedicineStorageDeleted)
        {
            Humanoid = humanoid;
            OnStorageDeleted = onStorageDeleted;
            OnFoodStorageDeleted = onFoodStorageDeleted;
            OnMedicineStorageDeleted = onMedicineStorageDeleted;
        }

        public HumanoidInstance Humanoid { get; }

        public Action<ResourceInstance> OnStorageDeleted { get; }

        public Action<ResourceInstance> OnFoodStorageDeleted { get; }

        public Action<ResourceInstance> OnMedicineStorageDeleted { get; }
    }
}
