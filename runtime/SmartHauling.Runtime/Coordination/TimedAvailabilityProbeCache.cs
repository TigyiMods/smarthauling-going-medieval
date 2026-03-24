namespace SmartHauling.Runtime;

internal static class TimedAvailabilityProbeCache
{
    internal readonly struct Entry
    {
        public Entry(int version, float expiresAt, bool value)
        {
            Version = version;
            ExpiresAt = expiresAt;
            Value = value;
        }

        public int Version { get; }

        public float ExpiresAt { get; }

        public bool Value { get; }
    }

    public static Entry Create(int version, float now, float lifetimeSeconds, bool value)
    {
        return new Entry(version, now + lifetimeSeconds, value);
    }

    public static bool TryGet(Entry entry, int version, float now, out bool value)
    {
        if (entry.Version == version && entry.ExpiresAt > now)
        {
            value = entry.Value;
            return true;
        }

        value = false;
        return false;
    }
}
