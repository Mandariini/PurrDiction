using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Predicted root of the player-name prefab.</summary>
public sealed class OwnerSyncVarRoot : PredictedIdentity<MixedNetworkProbeState>
{
    protected override void Simulate(ref MixedNetworkProbeState state, float delta) => state.ticks++;
}

/// <summary>
/// User-style name tag: the owner writes an owner-auth SyncVar and calls a ServerRpc from
/// OnSpawned, and every peer listens through onChanged.
/// </summary>
public sealed class OwnerSyncVarBehaviour : NetworkBehaviour
{
    public readonly SyncVar<string> displayName = new(ownerAuth: true);
    public string lastChanged;
    public string greeting;

    public static string NameFor(PlayerID player) => $"player-{player.id.value}";

    protected override void OnSpawned()
    {
        displayName.onChanged += OnNameChanged;

        if (isOwner && localPlayer.HasValue)
        {
            displayName.value = NameFor(localPlayer.Value);
            Greet(NameFor(localPlayer.Value));
        }
    }

    [ServerRpc]
    private void Greet(string value) => greeting = value;

    protected override void OnDespawned()
    {
        displayName.onChanged -= OnNameChanged;
    }

    private void OnNameChanged(string value) => lastChanged = value;
}

public static class OwnerSyncVarSignals
{
    private static readonly ScenarioStateLedger<Dictionary<uint, ulong>> Assignments = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void Assign(int scenarioIndex, uint[] instances, ulong[] owners)
    {
        var map = new Dictionary<uint, ulong>();
        for (var i = 0; i < instances.Length; i++)
            map[instances[i]] = owners[i];
        Assignments.TryAdd(scenarioIndex, 0, map);
    }

    public static bool TryGetAssignment(int scenarioIndex, out Dictionary<uint, ulong> map)
        => Assignments.TryGet(scenarioIndex, 0, out map);

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
/// Owner-auth SyncVars on NetworkBehaviours inside predicted prefabs, written by the owner in
/// OnSpawned, must reach every peer, the host's own value included (report: only the clients'
/// names arrive, never the host's).
/// </summary>
public sealed class OwnerSyncVarScenario : Scenario
{
    private const float Timeout = 30f;

    private GameObject _prefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<OwnerSyncVarRoot>("OwnerSyncVarPrefab");
        _prefab.AddComponent<OwnerSyncVarBehaviour>();
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        => RunSplit(ctx, RunClient, RunServer);

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var players = ctx.networkManager.players;
        var instances = new uint[players.Count];
        var owners = new ulong[players.Count];

        for (var i = 0; i < players.Count; i++)
        {
            var created = world.hierarchy.Create(_prefab, new Vector3(70 + i * 2, 0, 0), Quaternion.identity, players[i]);
            if (!created.HasValue)
                return ScenarioResult.Fail("name tag creation failed");
            instances[i] = created.Value.instanceId.value;
            owners[i] = players[i].id.value;
        }

        OwnerSyncVarSignals.Assign(ctx.scenarioIndex, instances, owners);

        string serverFailure = null;
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => AllNamesArrived(world, instances, owners, out _) && AllGreeted(world, instances, owners, out _),
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            AllNamesArrived(world, instances, owners, out var names);
            AllGreeted(world, instances, owners, out var greetings);
            serverFailure = string.Join("; ", new[] { names, greetings }.Where(m => m != null));
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => OwnerSyncVarSignals.TryGetReports(ctx.scenarioIndex, out var reports) &&
                      reports.Count >= ctx.expectedConnections,
                Timeout + 5f, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("not every client reported" + (serverFailure != null ? $"; server: {serverFailure}" : ""));
        }

        OwnerSyncVarSignals.TryGetReports(ctx.scenarioIndex, out var all);
        var failures = new List<string>();
        if (serverFailure != null)
            failures.Add($"server: {serverFailure}");
        foreach (var pair in all)
        {
            if (pair.Value != "ok")
                failures.Add($"{pair.Key}: {pair.Value}");
        }

        return failures.Count == 0
            ? ScenarioResult.Ok($"{instances.Length} owner names reached {all.Count} clients")
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        Dictionary<uint, ulong> map = null;
        string failure = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => OwnerSyncVarSignals.TryGetAssignment(ctx.scenarioIndex, out map), Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            failure = "no name tag assignment arrived";
        }

        if (failure == null)
        {
            var instances = new uint[map.Count];
            var owners = new ulong[map.Count];
            var i = 0;
            foreach (var pair in map)
            {
                instances[i] = pair.Key;
                owners[i] = pair.Value;
                i++;
            }

            try
            {
                await UniTaskUtils.WaitWithTimeout(() => AllNamesArrived(world, instances, owners, out _), Timeout, ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                AllNamesArrived(world, instances, owners, out failure);
            }
        }

        OwnerSyncVarSignals.Report(ctx.scenarioIndex, failure ?? "ok");
        return failure == null ? ScenarioResult.Ok() : ScenarioResult.Fail(failure);
    }

    private static bool AllGreeted(PredictionManager world, uint[] instances, ulong[] owners, out string failure)
    {
        var missing = new List<string>();
        for (var i = 0; i < instances.Length; i++)
        {
            var expected = $"player-{owners[i]}";
            if (!world.hierarchy.TryGetGameObject(new PredictedObjectID(instances[i]), out var go) || !go ||
                go.GetComponent<OwnerSyncVarBehaviour>().greeting != expected)
                missing.Add($"no ServerRpc from {expected}'s OnSpawned");
        }

        failure = missing.Count == 0 ? null : string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static bool AllNamesArrived(PredictionManager world, uint[] instances, ulong[] owners, out string failure)
    {
        var missing = new List<string>();
        for (var i = 0; i < instances.Length; i++)
        {
            var expected = $"player-{owners[i]}";
            if (!world.hierarchy.TryGetGameObject(new PredictedObjectID(instances[i]), out var go) || !go)
            {
                missing.Add($"{expected}: no instance");
                continue;
            }

            var behaviour = go.GetComponent<OwnerSyncVarBehaviour>();
            if (!behaviour || !behaviour.isSpawned)
                missing.Add($"{expected}: not spawned");
            else if (behaviour.displayName.value != expected || behaviour.lastChanged != expected)
                missing.Add($"{expected}: value='{behaviour.displayName.value}' changed='{behaviour.lastChanged}' owner={behaviour.owner}");
        }

        failure = missing.Count == 0 ? null : string.Join(", ", missing);
        return missing.Count == 0;
    }
}
