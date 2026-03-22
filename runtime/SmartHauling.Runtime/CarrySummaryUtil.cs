using System.Collections.Generic;
using System.Linq;
using NSMedieval;
using NSMedieval.Components;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Village.Map;

namespace SmartHauling.Runtime;

internal static class CarrySummaryUtil
{
    public static List<ResourceInstance> Snapshot(Storage? storage)
    {
        return storage == null
            ? new List<ResourceInstance>()
            : storage.GetResourcesWithoutLock()
                .Where(resource => resource != null && !resource.HasDisposed && resource.Amount > 0)
                .ToList();
    }

    public static string Summarize(Storage? storage)
    {
        return Summarize(Snapshot(storage));
    }

    public static string Summarize(IEnumerable<ResourceInstance> resources)
    {
        var summary = resources
            .Where(resource => resource != null && !resource.HasDisposed && resource.Amount > 0)
            .Select(resource => $"{resource.BlueprintId}:{resource.Amount}")
            .ToList();

        return summary.Count == 0 ? "<empty>" : string.Join(", ", summary);
    }
}
