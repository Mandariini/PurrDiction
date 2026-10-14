using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Predicted root of the pickup prefab.</summary>
public sealed class MirroredPickupRoot : PredictedIdentity<MixedNetworkProbeState>
{
    protected override void Simulate(ref MixedNetworkProbeState state, float delta) => state.ticks++;
}

/// <summary>
/// User-style pickup: the requester asks the server through a ServerRpc, the server deletes the
/// predicted object (or keeps it) and answers the requester with a TargetRpc from the same behaviour.
/// </summary>
public sealed class MirroredPickupBehaviour : NetworkBehaviour
{
    private static readonly HashSet<(int scenario, uint instance, int request)> Replies = new();

    public static bool HasReply(int scenarioIndex, uint instanceId, int request)
        => Replies.Contains((scenarioIndex, instanceId, request));

    [ServerRpc(requireOwnership: false)]
    public void Request(int scenarioIndex, uint instanceId, int request, bool deleteFirst, RPCInfo info = default)
    {
        if (deleteFirst && PredictionManager.TryGetInstance(gameObject.scene.handle, out var world))
            world.hierarchy.Delete(gameObject);

        Reply(info.sender, scenarioIndex, instanceId, request);
    }

    [TargetRpc]
    private void Reply(PlayerID target, int scenarioIndex, uint instanceId, int request)
        => Replies.Add((scenarioIndex, instanceId, request));
}

public static class MirroredTargetRpcSignals
{
    private static readonly ScenarioStateLedger<Dictionary<ulong, (uint keep, uint delete)>> Assignments = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void Assign(int scenarioIndex, ulong[] players, uint[] keep, uint[] delete)
    {
        var map = new Dictionary<ulong, (uint, uint)>();
        for (var i = 0; i < players.Length; i++)
            map[players[i]] = (keep[i], delete[i]);
        Assignments.TryAdd(scenarioIndex, 0, map);
    }

    public static bool TryGetAssignment(int scenarioIndex, PlayerID player, out (uint keep, uint delete) ids)
    {
        ids = default;
        return Assignments.TryGet(scenarioIndex, 0, out var map) && map.TryGetValue(player.id.value, out ids);
    }

    [ServerRpc(requireOwnership: false)]
    public static void Report(int scenarioIndex, string message, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!Reports.TryGet(scenarioIndex, 0, out var reports))
        {
            reports = new Dictionary<PlayerID, string>();
            Reports.TryAdd(scenarioIndex, 0, reports);
        }
        reports[info.sender] = message;
    }

    public static bool TryGetReports(int scenarioIndex, out Dictionary<PlayerID, string> reports)
        => Reports.TryGet(scenarioIndex, 0, out reports);
}

/// <summary>
/// NetworkBehaviours inside runtime-created predicted prefabs must answer a TargetRpc to every
/// requester, the host's own player included, also when the server deletes the predicted object
/// before replying (report: host never receives the TargetRpc of runtime-created pickups).
/// </summary>
public sealed class MirroredTargetRpcScenario : Scenario
{
    private const float Timeout = 30f;
    private const float ReplyTimeout = 10f;
    private const int KeepRequest = 1;
    private const int DeleteRequest = 2;

    private GameObject _prefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<MirroredPickupRoot>("MirroredPickupPrefab");
        _prefab.AddComponent<MirroredPickupBehaviour>();
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        => RunSplit(ctx, RunClient, RunServer);

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var players = ctx.networkManager.players;
        var ids = new ulong[players.Count];
        var keep = new uint[players.Count];
        var delete = new uint[players.Count];

        for (var i = 0; i < players.Count; i++)
        {
            var kept = world.hierarchy.Create(_prefab, new Vector3(50 + i * 2, 0, 0), Quaternion.identity);
            var deleted = world.hierarchy.Create(_prefab, new Vector3(50 + i * 2, 0, 3), Quaternion.identity);
            if (!kept.HasValue || !deleted.HasValue)
                return ScenarioResult.Fail("pickup creation failed");
            ids[i] = players[i].id.value;
            keep[i] = kept.Value.instanceId.value;
            delete[i] = deleted.Value.instanceId.value;
        }

        // A ServerRpc from a peer the server does not count as an observer yet is rejected.
        try
        {
            await UniTaskUtils.WaitWithTimeout(() =>
            {
                for (var i = 0; i < keep.Length; i++)
                {
                    if (!ObservedByAll(world, keep[i], players) || !ObservedByAll(world, delete[i], players))
                        return false;
                }
                return true;
            }, Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("the pickups never became observed by every player");
        }

        MirroredTargetRpcSignals.Assign(ctx.scenarioIndex, ids, keep, delete);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => MirroredTargetRpcSignals.TryGetReports(ctx.scenarioIndex, out var reports) &&
                      reports.Count >= ctx.expectedConnections,
                Timeout + 2 * ReplyTimeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("not every client reported");
        }

        MirroredTargetRpcSignals.TryGetReports(ctx.scenarioIndex, out var all);
        var failures = new List<string>();
        foreach (var pair in all)
        {
            if (pair.Value != "ok")
                failures.Add($"{pair.Key}: {pair.Value}");
        }

        return failures.Count == 0
            ? ScenarioResult.Ok($"{all.Count} requesters answered")
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var local = ctx.networkManager.localPlayer;
        (uint keep, uint delete) ids = default;
        MirroredPickupBehaviour kept = null, deleted = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => MirroredTargetRpcSignals.TryGetAssignment(ctx.scenarioIndex, local, out ids), Timeout, ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(
                () => TryGetSpawned(world, ids.keep, out kept) && TryGetSpawned(world, ids.delete, out deleted),
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            const string missing = "the mirrored pickups never spawned";
            MirroredTargetRpcSignals.Report(ctx.scenarioIndex, missing);
            return ScenarioResult.Fail(missing);
        }

        var failures = new List<string>();

        kept.Request(ctx.scenarioIndex, ids.keep, KeepRequest, false);
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => MirroredPickupBehaviour.HasReply(ctx.scenarioIndex, ids.keep, KeepRequest),
                ReplyTimeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            failures.Add("no TargetRpc reply from a kept runtime pickup");
        }

        deleted.Request(ctx.scenarioIndex, ids.delete, DeleteRequest, true);
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => MirroredPickupBehaviour.HasReply(ctx.scenarioIndex, ids.delete, DeleteRequest),
                ReplyTimeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            failures.Add("no TargetRpc reply from a runtime pickup the server deleted before replying");
        }

        var message = failures.Count == 0 ? "ok" : string.Join("; ", failures);
        MirroredTargetRpcSignals.Report(ctx.scenarioIndex, message);
        return failures.Count == 0 ? ScenarioResult.Ok() : ScenarioResult.Fail(message);
    }

    private static bool ObservedByAll(PredictionManager world, uint instanceId, IReadOnlyList<PlayerID> players)
    {
        if (!TryGetSpawned(world, instanceId, out var behaviour))
            return false;

        var observers = behaviour.observers;
        for (var i = 0; i < players.Count; i++)
        {
            bool found = false;
            for (var j = 0; j < observers.Count && !found; j++)
                found = observers[j] == players[i];
            if (!found)
                return false;
        }

        return true;
    }

    private static bool TryGetSpawned(PredictionManager world, uint instanceId, out MirroredPickupBehaviour behaviour)
    {
        behaviour = null;
        if (!world.hierarchy.TryGetGameObject(new PredictedObjectID(instanceId), out var go) || !go)
            return false;
        behaviour = go.GetComponent<MirroredPickupBehaviour>();
        return behaviour && behaviour.isSpawned;
    }
}
