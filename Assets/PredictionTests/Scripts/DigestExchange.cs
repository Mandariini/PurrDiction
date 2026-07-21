using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using PurrNet;
using UnityEngine;

public static class DigestExchange
{
    private static readonly Dictionary<int, Dictionary<PlayerID, string>> _reports = new();
    private static readonly Dictionary<int, Task<ScenarioResult>> _inFlight = new();

    /// <summary>
    /// Clients report their digest to the server; the server compares every report against
    /// its own digest and fails on any mismatch. Returns Ok on pure clients — the server
    /// result is authoritative for the run. Safe to call concurrently from both halves of a
    /// Host RunSplit: concurrent calls on the same channel share one server-side comparison.
    /// Aborts immediately (instead of waiting out the timeout) when a client disconnects.
    /// </summary>
    public static async UniTask<ScenarioResult> Compare(ScenarioContext ctx, int channel, string localDigest, float timeoutSeconds)
    {
        if (ctx.role == NetworkRole.Client)
            Report(channel, localDigest);

        if (!ctx.isServer)
            return ScenarioResult.Ok(localDigest);

        if (_inFlight.TryGetValue(channel, out var existing))
            return await existing;

        var task = CompareOnServer(ctx, channel, localDigest, timeoutSeconds).AsTask();
        _inFlight[channel] = task;
        try
        {
            return await task;
        }
        finally
        {
            _inFlight.Remove(channel);
        }
    }

    private static async UniTask<ScenarioResult> CompareOnServer(ScenarioContext ctx, int channel, string localDigest, float timeoutSeconds)
    {
        int expected = ctx.externalClientCount;
        var start = Time.realtimeSinceStartupAsDouble;

        while (true)
        {
            if (_reports.TryGetValue(channel, out var received) && received.Count >= expected)
                break;

            int connected = ConnectedExternalCount(ctx);
            if (connected < expected)
            {
                int got = received?.Count ?? 0;
                _reports.Remove(channel);
                return ScenarioResult.Fail(
                    $"digest aborted: only {connected}/{expected} clients still connected (got {got} reports)");
            }

            if (Time.realtimeSinceStartupAsDouble - start > timeoutSeconds)
            {
                int got = received?.Count ?? 0;
                _reports.Remove(channel);
                return ScenarioResult.Fail($"digest reports timeout: got {got}/{expected}");
            }

            ctx.cancellationToken.ThrowIfCancellationRequested();
            await UniTask.NextFrame();
        }

        var reports = _reports[channel];
        var failures = new List<string>();

        foreach (var (player, digest) in reports)
        {
            if (digest != localDigest)
                failures.Add($"player {player.id.value} diverged: '{digest}' != server '{localDigest}'");
        }

        _reports.Remove(channel);

        return failures.Count == 0
            ? ScenarioResult.Ok(localDigest)
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    private static int ConnectedExternalCount(ScenarioContext ctx)
    {
        var connected = ctx.networkManager.players;
        var localId = ctx.networkManager.localPlayer;
        bool isHost = ctx.role == NetworkRole.Host;

        int count = 0;
        for (int i = 0; i < connected.Count; i++)
        {
            if (isHost && connected[i] == localId)
                continue;
            count++;
        }

        return count;
    }

    [ServerRpc(requireOwnership: false)]
    private static void Report(int channel, string digest, RPCInfo info = default)
    {
        if (!_reports.TryGetValue(channel, out var set))
        {
            set = new Dictionary<PlayerID, string>();
            _reports[channel] = set;
        }
        set[info.sender] = digest;
    }
}
