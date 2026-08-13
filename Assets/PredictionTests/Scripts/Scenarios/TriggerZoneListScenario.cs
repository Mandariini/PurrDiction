using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Replication of the smoke-zone user report: a predicted rigidbody's trigger events maintain
/// a DisposableList of PredictedObjectID inside the identity's state, mutated through
/// currentState from user-style enter/exit handlers. Two overlapping zones run the same
/// tracker — one spawned as a predicted prefab, one scene-authored before connect — to cover
/// both registration paths. One ball drops into the zones and rests there for the whole run;
/// per-player owned walkers oscillate across the boundary, mispredicting their crossings on
/// remote peers, then park inside. The reported failure mode is the client's list showing an
/// entry for a frame and then reading empty (with "Not enough bits in the buffer" during frame
/// decode). The scenario samples the resting ball's tracked membership in both zones every
/// frame during the churn window and fails on any empty/disposed sample, on missing final
/// membership, or on a cross-peer digest mismatch. Harness log capture turns any decode
/// exception into a failure.
/// </summary>
public class TriggerZoneListScenario : Scenario
{
    [SerializeField] private float _churnSeconds = 15f;
    [SerializeField] private float _timeout = 120f;

    private const int DigestChannel = 1500;
    private static readonly Vector3 Base = new Vector3(500f, 0f, 500f);

    private GameObject _zonePrefab;
    private GameObject _ballPrefab;
    private GameObject _walkerPrefab;
    private SmokeZoneTracker _sceneTracker;
    private int _zonePrefabId;
    private int _ballPrefabId;
    private int _walkerPrefabId;
    private ulong _parkTick;
    private bool _includeInstantiated;
    private bool _undiscoveredWarningSeen;

    private void OnLogMessage(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Warning &&
            condition.Contains("SmokeZoneScene") &&
            condition.Contains("not discovered"))
        {
            _undiscoveredWarningSeen = true;
        }
    }

    private static void AddZoneComponents(GameObject go)
    {
        var rigidbody = go.AddComponent<Rigidbody>();
        rigidbody.isKinematic = true;
        rigidbody.useGravity = false;
        var collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(8f, 4f, 8f);
        go.AddComponent<PredictedTransform>();
        go.AddComponent<PredictedRigidbody>();
        go.AddComponent<SmokeZoneTracker>();
    }

    private static void EnableInstantiatedSceneObjectDiscovery(NetworkManager manager)
    {
        var rulesField = typeof(NetworkRules).GetField(
            "_defaultSpawnRules",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (rulesField == null)
        {
            Debug.LogWarning("[TriggerZoneList] _defaultSpawnRules field not found; discovery flag not applied");
            return;
        }

        object rules = rulesField.GetValue(manager.networkRules);
        var includeField = rules.GetType().GetField("includeInstantiatedSceneObjects");
        includeField.SetValue(rules, true);
        rulesField.SetValue(manager.networkRules, rules);
    }

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _includeInstantiated = CommandLineUtils.HasFlag("-includeInstantiatedSceneObjects");
        if (_includeInstantiated)
            EnableInstantiatedSceneObjectDiscovery(manager);

        Application.logMessageReceived += OnLogMessage;

        var floor = new GameObject("TriggerZoneFloor");
        var floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(50f, 1f, 50f);
        floor.transform.position = Base + new Vector3(0f, -0.5f, 0f);
        floor.AddComponent<PredictedTransform>();

        var sceneZone = new GameObject("SmokeZoneScene");
        sceneZone.SetActive(false);
        sceneZone.transform.position = Base + new Vector3(0f, 2.25f, 0f);
        AddZoneComponents(sceneZone);
        sceneZone.SetActive(true);
        _sceneTracker = sceneZone.GetComponent<SmokeZoneTracker>();

        _zonePrefab = new GameObject("SmokeZone");
        _zonePrefab.SetActive(false);
        AddZoneComponents(_zonePrefab);
        DontDestroyOnLoad(_zonePrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _zonePrefab);

        _ballPrefab = new GameObject("SmokeZoneBall");
        _ballPrefab.SetActive(false);
        var ballRigidbody = _ballPrefab.AddComponent<Rigidbody>();
        ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _ballPrefab.AddComponent<SphereCollider>();
        _ballPrefab.AddComponent<PredictedTransform>();
        _ballPrefab.AddComponent<PredictedRigidbody>();
        DontDestroyOnLoad(_ballPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _ballPrefab);

        _walkerPrefab = new GameObject("ZoneWalker");
        _walkerPrefab.SetActive(false);
        var walkerRigidbody = _walkerPrefab.AddComponent<Rigidbody>();
        walkerRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _walkerPrefab.AddComponent<SphereCollider>();
        _walkerPrefab.AddComponent<PredictedTransform>();
        _walkerPrefab.AddComponent<PredictedRigidbody>();
        _walkerPrefab.AddComponent<ZoneWalker>();
        DontDestroyOnLoad(_walkerPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _walkerPrefab);

        ZoneWalker.baseX = Base.x;
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLogMessage;
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        SmokeZoneTracker.ResetCounters();

        var tickRate = ctx.predictionManager.tickRate;
        _parkTick = startTick + (ulong)Mathf.CeilToInt(_churnSeconds * tickRate);
        ZoneWalker.parkTick = _parkTick;
        ZoneWalker.halfPeriodTicks = (uint)Mathf.Max(1, Mathf.CeilToInt(1.5f * tickRate));
    }

    private SmokeZoneTracker GetSpawnedTracker(PredictionManager pm)
    {
        return TryGetSingleInstanceId(pm, _zonePrefabId, out var zoneId) &&
               zoneId.TryGetComponent<SmokeZoneTracker>(pm, out var tracker)
            ? tracker
            : null;
    }

    private static int SampleBallMembership(SmokeZoneTracker zone, PredictedObjectID ballId)
    {
        if (!zone)
            return -1;

        ref var state = ref zone.currentState;
        if (state.insideIds.isDisposed)
            return -2;

        return state.insideIds.Contains(ballId) ? 1 : 0;
    }

    private static bool TryGetSingleInstanceId(PredictionManager pm, int prefabId, out PredictedObjectID id)
    {
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == prefabId)
            {
                id = details.instanceId;
                return true;
            }
        }

        id = default;
        return false;
    }

    private static void CollectInstanceIds(PredictionManager pm, int prefabId, List<PredictedObjectID> ids)
    {
        ids.Clear();
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == prefabId)
                ids.Add(details.instanceId);
        }
    }

    private static bool TracksAll(SmokeZoneTracker zone, PredictedObjectID ballId, List<PredictedObjectID> walkerIds)
    {
        if (!zone)
            return false;

        ref var state = ref zone.currentState;
        if (state.insideIds.isDisposed || !state.insideIds.Contains(ballId))
            return false;

        for (var i = 0; i < walkerIds.Count; i++)
        {
            if (!state.insideIds.Contains(walkerIds[i]))
                return false;
        }

        return true;
    }

    private void LogSceneZoneDiagnostics(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var sceneGo = _sceneTracker.gameObject;
        var scene = sceneGo.scene;

        var discovered = new List<PredictedIdentity>();
        SceneObjectsModule.GetScenePredictedIdentities(scene, discovered);
        bool inDiscovery = discovered.Contains(_sceneTracker);

        PurrSceneInfo sceneInfo = null;
        var roots = scene.GetRootGameObjects();
        for (var i = 0; i < roots.Length; i++)
        {
            if (roots[i].TryGetComponent(out sceneInfo))
                break;
        }

        bool inSceneInfoRoots = sceneInfo && sceneInfo.rootGameObjects != null &&
                                sceneInfo.rootGameObjects.Contains(sceneGo);

        bool resolvable = PredictionManager.TryGetClosestPredictedID(sceneGo, out var closest);

        Debug.Log(
            $"[TriggerZoneDiag] {ctx.role} sceneZone id={_sceneTracker.id.objectId.instanceId.value}:{_sceneTracker.id.componentId.value} " +
            $"discovered={inDiscovery} discoveredCount={discovered.Count} " +
            $"purrSceneInfo={(sceneInfo ? sceneInfo.rootGameObjects?.Count ?? -1 : -1)} inSceneInfoRoots={inSceneInfoRoots} " +
            $"resolvableId={resolvable}:{closest.objectId.instanceId.value} " +
            $"managerScene={pm.gameObject.scene.handle == scene.handle} " +
            $"stateDisposed={_sceneTracker.currentState.insideIds.isDisposed}");
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_zonePrefab, out _zonePrefabId);
        pm.TryGetPrefab(_ballPrefab, out _ballPrefabId);
        pm.TryGetPrefab(_walkerPrefab, out _walkerPrefabId);

        LogSceneZoneDiagnostics(ctx);

        int expectedWalkers = ctx.expectedConnections;

        if (ctx.isServer)
        {
            if (!pm.hierarchy.Create(_zonePrefab, Base + new Vector3(0f, 2.25f, 0f), Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create smoke zone");

            if (!pm.hierarchy.Create(_ballPrefab, Base + new Vector3(0f, 6f, 3f), Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create ball");

            var owners = new List<PlayerID>(pm.players.players);
            owners.Sort((a, b) => a.id.value.CompareTo(b.id.value));

            if (owners.Count != expectedWalkers)
                return ScenarioResult.Fail($"expected {expectedWalkers} players, saw {owners.Count}");

            for (var i = 0; i < owners.Count; i++)
            {
                var position = Base + new Vector3(-6f, 0.6f, -3f + i * 2f);
                if (!pm.hierarchy.Create(_walkerPrefab, position, Quaternion.identity, owners[i]).HasValue)
                    return ScenarioResult.Fail($"failed to create walker {i}");
            }
        }

        PredictedObjectID ballId = default;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => TryGetSingleInstanceId(pm, _ballPrefabId, out ballId) &&
                      PredictionTestUtils.CountInstances(pm, _walkerPrefabId) == expectedWalkers &&
                      SampleBallMembership(GetSpawnedTracker(pm), ballId) == 1 &&
                      (!_includeInstantiated || SampleBallMembership(_sceneTracker, ballId) == 1),
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                "ball was never tracked inside the zone: " +
                $"spawned={SampleBallMembership(GetSpawnedTracker(pm), ballId)} " +
                $"scene={SampleBallMembership(_sceneTracker, ballId)} " +
                $"walkers={PredictionTestUtils.CountInstances(pm, _walkerPrefabId)}/{expectedWalkers} " +
                $"enters={SmokeZoneTracker.enterFires} exits={SmokeZoneTracker.exitFires}");
        }

        var spawnedTracker = GetSpawnedTracker(pm);

        int samples = 0;
        int lostSpawned = 0;
        int lostScene = 0;
        int disposedSamples = 0;
        ulong firstBadTick = 0;

        while (pm.time.tick < _parkTick)
        {
            await UniTask.NextFrame(ctx.cancellationToken);

            samples++;

            int spawnedMembership = SampleBallMembership(spawnedTracker, ballId);
            int sceneMembership = _includeInstantiated ? SampleBallMembership(_sceneTracker, ballId) : 1;

            if (spawnedMembership == -2 || sceneMembership == -2)
            {
                disposedSamples++;
                if (firstBadTick == 0)
                    firstBadTick = pm.time.tick;
            }

            if (spawnedMembership == 0)
            {
                lostSpawned++;
                if (firstBadTick == 0)
                    firstBadTick = pm.time.tick;
            }

            if (sceneMembership == 0)
            {
                lostScene++;
                if (firstBadTick == 0)
                    firstBadTick = pm.time.tick;
            }
        }

        var walkerIds = new List<PredictedObjectID>();
        CollectInstanceIds(pm, _walkerPrefabId, walkerIds);

        bool converged = false;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => TracksAll(spawnedTracker, ballId, walkerIds) &&
                      (!_includeInstantiated || TracksAll(_sceneTracker, ballId, walkerIds)),
                30f,
                ctx.cancellationToken);
            converged = true;
        }
        catch (TimeoutException)
        {
        }

        await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);

        var sceneDigest = _includeInstantiated ? ZoneDigest(_sceneTracker) : "unregistered";
        var digest = $"spawned[{ZoneDigest(spawnedTracker)}]scene[{sceneDigest}]";
        var report =
            $"samples={samples} lostSpawned={lostSpawned} lostScene={lostScene} disposed={disposedSamples} " +
            $"converged={converged} warned={_undiscoveredWarningSeen} " +
            $"enters={SmokeZoneTracker.enterFires} exits={SmokeZoneTracker.exitFires} " +
            $"firstBadTick={firstBadTick} digest={digest}";

        Debug.Log($"[TriggerZoneList] {ctx.role} {report}");

        var digestResult = await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);

        if (lostSpawned > 0 || lostScene > 0 || disposedSamples > 0)
            return ScenarioResult.Fail($"tracked list lost the resting ball while it stayed inside: {report}");

        if (!converged)
            return ScenarioResult.Fail($"parked walkers never all tracked inside the zone(s): {report}");

        if (_includeInstantiated)
        {
            if (_undiscoveredWarningSeen)
                return ScenarioResult.Fail($"scene zone warned as undiscovered despite includeInstantiatedSceneObjects: {report}");
        }
        else
        {
            if (!_undiscoveredWarningSeen)
                return ScenarioResult.Fail($"no undiscovered-identity warning for the unregistered scene zone: {report}");

            if (!_sceneTracker.currentState.insideIds.isDisposed)
                return ScenarioResult.Fail($"unregistered scene zone unexpectedly has initialized state: {report}");
        }

        if (!digestResult.success)
            return digestResult;

        return ScenarioResult.Ok(report);
    }

    private static string ZoneDigest(SmokeZoneTracker zone)
    {
        if (!zone)
            return "missing";

        ref var state = ref zone.currentState;
        if (state.insideIds.isDisposed)
            return "disposed";

        var ids = new List<uint>();
        for (var i = 0; i < state.insideIds.Count; i++)
            ids.Add(state.insideIds[i].instanceId.value);
        ids.Sort();

        var sb = new StringBuilder();
        sb.Append($"count={state.insideIds.Count};ids=");
        for (var i = 0; i < ids.Count; i++)
            sb.Append(ids[i]).Append(',');
        return sb.ToString();
    }
}
