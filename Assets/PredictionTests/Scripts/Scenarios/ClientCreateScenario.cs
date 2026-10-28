using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Player pawn whose input carries a spawn-click counter, like the docs' PlayerSpawner.</summary>
public sealed class ClientCreatePawn : PredictedIdentity<ClientCreatePawn.SpawnInput, ClientCreatePawn.SpawnerState>
{
    public static GameObject inputSpawnPrefab;

    // Bumped by a "UI button" outside the simulation; only input carries it into Simulate.
    public uint spawnClicks;

    // Diagnostics, not state: how far a client-only phantom got before the rollback removed it.
    public int inputUpdates;
    public int simulations;

    // Diagnostics, not state: the first tick the owner gathered input for, and on the server the
    // first tick its input arrived for plus every later tick it was missing.
    public ulong ownerFirstInputTick;
    public ulong serverFirstInputTick;
    public int serverMissedTicks;

    public struct SpawnInput : IPredictedData
    {
        public uint spawnCount;
        public ulong tick;
        public void Dispose() { }
    }

    public struct SpawnerState : IPredictedData<SpawnerState>
    {
        public uint handledSpawns;
        public void Dispose() { }
    }

    protected override void UpdateInput(ref SpawnInput input)
    {
        inputUpdates++;
        input.spawnCount = spawnClicks;
    }

    protected override void GetFinalInput(ref SpawnInput input)
    {
        input.tick = predictionManager.localTick;
        if (ownerFirstInputTick == 0)
            ownerFirstInputTick = input.tick;
    }

    protected override void Simulate(SpawnInput input, ref SpawnerState state, float delta)
    {
        simulations++;

        if (predictionManager.isServer)
        {
            if (input.tick == predictionManager.localTick)
            {
                if (serverFirstInputTick == 0)
                    serverFirstInputTick = input.tick;
            }
            else if (serverFirstInputTick != 0)
                serverMissedTicks++;
        }
        while (state.handledSpawns < input.spawnCount)
        {
            state.handledSpawns++;
            predictionManager.hierarchy.Create(inputSpawnPrefab, transform.position + Vector3.up, Quaternion.identity, owner);
        }
    }
}

public struct ClientCreateSpawnedState : IPredictedData<ClientCreateSpawnedState>
{
    public uint ticks;
    public void Dispose() { }
}

/// <summary>What a spawn request creates; kind tells the input path (1) from the ServerRpc path (2).</summary>
public sealed class ClientCreateSpawned : PredictedIdentity<ClientCreateSpawnedState>
{
    public int kind;

    protected override void Simulate(ref ClientCreateSpawnedState state, float delta) => state.ticks++;
}

public static class ClientCreateSignals
{
    private static readonly ScenarioStateLedger<Dictionary<ulong, uint>> Pawns = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, ulong>> FirstInputs = new();

    public static GameObject rpcSpawnPrefab;
    public static PredictionManager world;

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void AssignPawns(int scenarioIndex, ulong[] players, uint[] pawns)
    {
        var map = new Dictionary<ulong, uint>();
        for (var i = 0; i < players.Length; i++)
            map[players[i]] = pawns[i];
        Pawns.TryAdd(scenarioIndex, 0, map);
    }

    public static bool TryGetPawn(int scenarioIndex, PlayerID player, out uint pawn)
    {
        pawn = 0;
        return Pawns.TryGet(scenarioIndex, 0, out var map) && map.TryGetValue(player.id.value, out pawn);
    }

    // The docs' non-predicted path: a menu button asks the server, the server creates.
    [ServerRpc(requireOwnership: false)]
    public static void RequestSpawn(int scenarioIndex, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex) || !world) return;
        world.hierarchy.Create(rpcSpawnPrefab, new Vector3(130, 0, info.sender.id.value * 2f), Quaternion.identity, info.sender);
    }

    [ServerRpc(requireOwnership: false)]
    public static void ReportFirstInput(int scenarioIndex, ulong tick, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!FirstInputs.TryGet(scenarioIndex, 0, out var ticks))
        {
            ticks = new Dictionary<PlayerID, ulong>();
            FirstInputs.TryAdd(scenarioIndex, 0, ticks);
        }
        ticks[info.sender] = tick;
    }

    public static bool TryGetFirstInput(int scenarioIndex, PlayerID player, out ulong tick)
    {
        tick = 0;
        return FirstInputs.TryGet(scenarioIndex, 0, out var ticks) && ticks.TryGetValue(player, out tick);
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
/// Discord report (AngryCeltic / Peanut, 2026-10-27): a player spawner calls hierarchy.Create from a
/// UI button. On the host the player stays; on a client it shows for a moment, goes back to the pool,
/// and the server logs "Input entry rejected ... known=False". Phase A replays that call on every
/// peer outside Simulate; phases B and C run the two documented alternatives (input counter in
/// Simulate, ServerRpc) and require their objects to appear and stay.
/// </summary>
public sealed class ClientCreateScenario : Scenario
{
    private const float Timeout = 20f;
    private const float WatchSeconds = 2f;

    private GameObject _pawnPrefab;
    private GameObject _inputSpawnPrefab;
    private GameObject _rpcSpawnPrefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _pawnPrefab = PredictionTestUtils.CreatePrefab<ClientCreatePawn>("ClientCreatePawn");
        _inputSpawnPrefab = PredictionTestUtils.CreatePrefab<ClientCreateSpawned>("ClientCreateInputSpawned");
        _inputSpawnPrefab.GetComponent<ClientCreateSpawned>().kind = 1;
        _rpcSpawnPrefab = PredictionTestUtils.CreatePrefab<ClientCreateSpawned>("ClientCreateRpcSpawned");
        _rpcSpawnPrefab.GetComponent<ClientCreateSpawned>().kind = 2;

        PredictionTestUtils.RegisterPrefab(ctx, _pawnPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _inputSpawnPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _rpcSpawnPrefab);

        ClientCreatePawn.inputSpawnPrefab = _inputSpawnPrefab;
        ClientCreateSignals.rpcSpawnPrefab = _rpcSpawnPrefab;
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        => RunSplit(ctx, RunClient, RunServer);

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        ClientCreateSignals.world = world;

        var rejected = new List<string>();
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning && message.Contains("Ignoring input from"))
                rejected.Add(message);
        }

        var players = ctx.networkManager.players;
        var playerIds = new ulong[players.Count];
        var pawns = new uint[players.Count];
        for (var i = 0; i < players.Count; i++)
        {
            var created = world.hierarchy.Create(_pawnPrefab, new Vector3(120, 0, i * 2f), Quaternion.identity, players[i]);
            if (!created.HasValue)
                return ScenarioResult.Fail("pawn creation failed");
            playerIds[i] = players[i].id.value;
            pawns[i] = created.Value.instanceId.value;
        }

        Application.logMessageReceived += OnLog;
        try
        {
            ClientCreateSignals.AssignPawns(ctx.scenarioIndex, playerIds, pawns);

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => ClientCreateSignals.TryGetReports(ctx.scenarioIndex, out var reports) &&
                          reports.Count >= ctx.expectedConnections,
                    Timeout * 3, ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("not every client reported");
            }
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
        }

        ClientCreateSignals.TryGetReports(ctx.scenarioIndex, out var all);
        var failures = new List<string>();
        var lines = new List<string>();
        foreach (var pair in all)
        {
            var player = pair.Key;
            bool host = ctx.role == NetworkRole.Host && player == ctx.networkManager.localPlayer;
            // The server only ever holds what it created: the pawn, plus the host's own button press.
            int pawnsOwned = CountOwned<ClientCreatePawn>(world, player, null);
            int inputSpawns = CountOwned<ClientCreateSpawned>(world, player, s => s.kind == 1);
            int rpcSpawns = CountOwned<ClientCreateSpawned>(world, player, s => s.kind == 2);
            int expectedPawns = host ? 2 : 1;
            string serverView = $"server pawns={pawnsOwned}/{expectedPawns} input={inputSpawns}/1 rpc={rpcSpawns}/1";

            // How much of the pawn's input reached the server in time, across the roster changes of the
            // pawn's arrival and the phantom. Informational: a late input and a dropped upload look the
            // same from here, and a tight input lead makes late ones normal at high tick rates.
            // UploadRoundTripTests.RosterChangeLeavesSentTicksAsTheServerAcceptedThem guards the drops.
            string inputView = "pawn input: no pawn";
            var pawnIndex = Array.IndexOf(playerIds, player.id.value);
            if (pawnIndex >= 0 && new PredictedObjectID(pawns[pawnIndex]).TryGetComponent<ClientCreatePawn>(world, out var serverPawn))
            {
                ClientCreateSignals.TryGetFirstInput(ctx.scenarioIndex, player, out var ownerFirst);
                inputView = $"pawn input: owner first={ownerFirst} server first={serverPawn.serverFirstInputTick} missed={serverPawn.serverMissedTicks}";
            }

            lines.Add($"{player}: {pair.Value}; {serverView}; {inputView}");

            if (!pair.Value.StartsWith("ok"))
                failures.Add($"{player}: {pair.Value}");
            if (pawnsOwned != expectedPawns || inputSpawns != 1 || rpcSpawns != 1)
                failures.Add($"{player}: {serverView}");
        }

        int rejectedCount = rejected.Count;
        string firstRejected = rejectedCount > 0 ? rejected[0] : null;

        // Leave nothing behind for later scenarios. Clients keep sending input for their pawns until the
        // delete reaches them; that stale input is expected and must not be reported.
        rejected.Clear();
        Application.logMessageReceived += OnLog;
        try
        {
            DeleteAll<ClientCreatePawn>(world);
            DeleteAll<ClientCreateSpawned>(world);
            await UniTask.Delay(TimeSpan.FromSeconds(1.5f), cancellationToken: ctx.cancellationToken);
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
        }

        if (rejected.Count > 0)
            failures.Add($"{rejected.Count} input warnings after deleting the pawns, first='{rejected[0]}'");

        string summary = $"{string.Join(" | ", lines)} | server input warnings={rejectedCount}" +
                         (firstRejected != null ? $" first='{firstRejected}'" : "") +
                         $", after delete={rejected.Count}";

        Debug.Log($"[ClientCreateScenario] server: {summary}");

        return failures.Count == 0
            ? ScenarioResult.Ok(summary)
            : ScenarioResult.Fail($"{string.Join(" | ", failures)} || {summary}");
    }

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var me = ctx.networkManager.localPlayer;
        bool host = ctx.role == NetworkRole.Host;
        uint pawnId = 0;
        ClientCreatePawn pawn = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ClientCreateSignals.TryGetPawn(ctx.scenarioIndex, me, out pawnId) &&
                      new PredictedObjectID(pawnId).TryGetComponent(world, out pawn) && pawn.isOwner,
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return Finish(ctx, "no owned pawn arrived");
        }

        // Settle so the phantom below is not created inside a catch-up burst.
        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: ctx.cancellationToken);

        // Phase A: the report's spawner, a button callback calling hierarchy.Create directly. A client
        // must be told why its object disappears; a host is the server, so its create is real.
        int clientWarnings = 0;
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning && message.Contains("was created on a client outside Simulate"))
                clientWarnings++;
        }

        PhantomResult phantom;
        Application.logMessageReceived += OnLog;
        try
        {
            phantom = await CreateOutsideSimulate(ctx, world, me);
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
        }

        if (phantom.failure != null)
            return Finish(ctx, phantom.failure);

        int expectedWarnings = host ? 0 : 1;
        bool phantomOk = (host ? phantom.stayed : !phantom.stayed) && clientWarnings == expectedWarnings;
        string phaseA = phantom.stayed
            ? $"A stayed (host={host}, id={phantom.id})"
            : $"A removed after {phantom.ms:F0} ms/{phantom.ticks} ticks/{phantom.frames} frames, phantom id={phantom.id} inputs={phantom.inputUpdates} sims={phantom.simulations} (host={host}, resurrected={phantom.resurrected})";

        // Phase B: the same click as input; Simulate creates on both sides.
        pawn.spawnClicks++;
        var inputSpawn = await WatchOwnedSpawn(ctx, world, me, 1);

        // Phase C: the same click as a ServerRpc; only the server creates.
        ClientCreateSignals.RequestSpawn(ctx.scenarioIndex);
        var rpcSpawn = await WatchOwnedSpawn(ctx, world, me, 2);

        ClientCreateSignals.ReportFirstInput(ctx.scenarioIndex, pawn.ownerFirstInputTick);
        string summary = $"{phaseA}, client warnings={clientWarnings}/{expectedWarnings}; B {inputSpawn}; C {rpcSpawn}";
        bool ok = phantomOk && inputSpawn.StartsWith("stayed") && rpcSpawn.StartsWith("stayed") && !phantom.resurrected;
        return Finish(ctx, ok ? $"ok {summary}" : summary);
    }

    private struct PhantomResult
    {
        public bool stayed;
        public bool resurrected;
        public int frames;
        public long ticks;
        public double ms;
        public int inputUpdates;
        public int simulations;
        public uint id;
        public string failure;
    }

    private async UniTask<PhantomResult> CreateOutsideSimulate(ScenarioContext ctx, PredictionManager world, PlayerID me)
    {
        if (world.isSimulating)
            return new PhantomResult { failure = "phantom create ran inside the simulation" };

        var created = world.hierarchy.Create(_pawnPrefab, new Vector3(125, 0, me.id.value * 2f), Quaternion.identity, me);
        if (!created.HasValue || !world.hierarchy.TryGetGameObject(created.Value, out var go) || !go || !go.activeInHierarchy)
            return new PhantomResult { failure = "phantom create returned nothing on the creating peer" };

        var id = created.Value;
        var phantomPawn = go.GetComponent<ClientCreatePawn>();
        phantomPawn.inputUpdates = 0;
        phantomPawn.simulations = 0;
        int startFrame = Time.frameCount;
        ulong startTick = world.localTick;
        double startTime = Time.realtimeSinceStartupAsDouble;
        var result = new PhantomResult { stayed = true, id = id.instanceId.value };

        var deadline = Time.realtimeSinceStartupAsDouble + 5.0;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            await UniTask.NextFrame();
            if (CountOwned<ClientCreatePawn>(world, me, null) < 2)
            {
                result.stayed = false;
                result.frames = Time.frameCount - startFrame;
                result.ticks = (long)world.localTick - (long)startTick;
                result.ms = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
                result.inputUpdates = phantomPawn.inputUpdates;
                result.simulations = phantomPawn.simulations;
                break;
            }
        }

        if (!result.stayed)
        {
            // A removed phantom has to stay removed; the pawn the server gave this player is the only one.
            var watchEnd = Time.realtimeSinceStartupAsDouble + WatchSeconds;
            while (Time.realtimeSinceStartupAsDouble < watchEnd)
            {
                await UniTask.NextFrame();
                if (CountOwned<ClientCreatePawn>(world, me, null) > 1)
                    result.resurrected = true;
            }
        }

        return result;
    }

    private static async UniTask<string> WatchOwnedSpawn(ScenarioContext ctx, PredictionManager world, PlayerID me, int kind)
    {
        double startTime = Time.realtimeSinceStartupAsDouble;
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CountOwned<ClientCreateSpawned>(world, me, s => s.kind == kind) > 0, Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return "never appeared";
        }

        double appearMs = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
        int gaps = 0;
        int maxCount = 0;
        var watchEnd = Time.realtimeSinceStartupAsDouble + WatchSeconds;
        while (Time.realtimeSinceStartupAsDouble < watchEnd)
        {
            await UniTask.NextFrame();
            int count = CountOwned<ClientCreateSpawned>(world, me, s => s.kind == kind);
            if (count == 0)
                gaps++;
            maxCount = Math.Max(maxCount, count);
        }

        return gaps == 0 && maxCount == 1
            ? $"stayed (appeared after {appearMs:F0} ms)"
            : $"unstable (appeared after {appearMs:F0} ms, missing in {gaps} frames, max count {maxCount})";
    }

    private static ScenarioResult Finish(ScenarioContext ctx, string message)
    {
        Debug.Log($"[ClientCreateScenario] client {ctx.networkManager.localPlayer}: {message}");
        ClientCreateSignals.Report(ctx.scenarioIndex, message);
        return message.StartsWith("ok") ? ScenarioResult.Ok(message) : ScenarioResult.Fail(message);
    }

    private static bool IsLive(PredictionManager world, PredictedObjectID id, out GameObject go)
        => world.hierarchy.TryGetGameObject(id, out go) && go && go.activeInHierarchy;

    private static int CountOwned<T>(PredictionManager world, PlayerID player, Func<T, bool> filter) where T : PredictedIdentity
    {
        ref var state = ref world.hierarchy.currentState;
        int count = 0;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (!details.isRootRecord || details.owner != player)
                continue;
            if (details.instanceId.TryGetComponent<T>(world, out var component) && (filter == null || filter(component)))
                count++;
        }

        return count;
    }

    private static void DeleteAll<T>(PredictionManager world) where T : PredictedIdentity
    {
        ref var state = ref world.hierarchy.currentState;
        var ids = new List<PredictedObjectID>();
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.isRootRecord && details.instanceId.TryGetComponent<T>(world, out _))
                ids.Add(details.instanceId);
        }

        foreach (var id in ids)
            world.hierarchy.Delete(id);
    }
}
