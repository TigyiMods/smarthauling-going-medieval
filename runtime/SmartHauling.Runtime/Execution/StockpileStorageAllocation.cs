using NSMedieval;

namespace SmartHauling.Runtime;

internal sealed class StockpileStorageAllocation
{
    public StockpileStorageAllocation(IStorage storage, int requestedAmount)
    {
        Storage = storage;
        RequestedAmount = requestedAmount;
    }

    public IStorage Storage { get; }

    public int RequestedAmount { get; }
}
