using System.Collections.Generic;
using PurrNet;

public static class DigestGate
{
    private static readonly Dictionary<int, ulong> _digestTickByChannel = new();

    public static void Reset()
    {
        _digestTickByChannel.Clear();
    }

    public static bool TryGetDigestTick(int channel, out ulong tick)
    {
        return _digestTickByChannel.TryGetValue(channel, out tick);
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastDigestTick(int channel, ulong tick)
    {
        _digestTickByChannel[channel] = tick;
    }
}
