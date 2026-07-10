using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Reproduces soft-correction accumulators leaking from one pooled lifetime into the next.
/// The probe continuously creates a local-only pose error, is despawned, and is eventually
/// reused after the rollback pool hands it back to the prefab pool.
/// </summary>
public sealed class SoftCorrectionPoolReuseScenario : Scenario
{
    private const float TimeoutSeconds = 90f;

    private GameObject _driverPrefab;
    private GameObject _probePrefab;
    private int _driverPrefabId;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = new GameObject(nameof(SoftCorrectionPoolProbe));
        _probePrefab.SetActive(false);
        var probe = _probePrefab.AddComponent<SoftCorrectionPoolProbe>();
        probe.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
        UnityEngine.Object.DontDestroyOnLoad(_probePrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab, pooled: true, warmupCount: 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<SoftCorrectionPoolDriver>(
            nameof(SoftCorrectionPoolDriver));
        _driverPrefab.GetComponent<SoftCorrectionPoolDriver>().probePrefab = _probePrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        SoftCorrectionPoolProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"never reached scheduled start tick {_startTick}");
        }

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create soft-correction pool driver");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => DriverFinished(pm),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pool driver timed out: priorLifetimeReuses={SoftCorrectionPoolProbe.priorLifetimeReuses}, " +
                $"verifiedCorrections={SoftCorrectionPoolProbe.verifiedCorrections}, " +
                $"staleCorrections={SoftCorrectionPoolProbe.staleCorrectionReuses}");
        }

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        if (SoftCorrectionPoolProbe.verifiedCorrections == 0)
            return ScenarioResult.Fail("the client never received a soft-correction target");

        if (SoftCorrectionPoolProbe.priorLifetimeReuses == 0)
            return ScenarioResult.Fail("the probe was never reused after a completed pooled lifetime");

        if (SoftCorrectionPoolProbe.staleCorrectionReuses != 0)
        {
            return ScenarioResult.Fail(
                $"{SoftCorrectionPoolProbe.staleCorrectionReuses}/" +
                $"{SoftCorrectionPoolProbe.priorLifetimeReuses} pooled lifetimes started with a stale correction");
        }

        return ScenarioResult.Ok();
    }

    private bool DriverFinished(PredictionManager pm)
    {
        ref var hierarchyState = ref pm.hierarchy.currentState;
        for (var i = 0; i < hierarchyState.spawnedPrefabs.Count; i++)
        {
            var details = hierarchyState.spawnedPrefabs[i];
            if (details.prefabId != _driverPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<SoftCorrectionPoolDriver>(pm, out var driver))
                return driver.currentState.completed >= SoftCorrectionPoolDriver.TotalLifetimes;
        }

        return false;
    }
}

public sealed class SoftCorrectionPoolDriver : PredictedIdentity<SoftCorrectionPoolDriver.DriverState>
{
    public const int TotalLifetimes = 18;
    private const uint LifetimeTicks = 18;
    private const uint GapTicks = 3;

    public GameObject probePrefab;

    public struct DriverState : IPredictedData<DriverState>
    {
        public uint phaseTicks;
        public uint activeId;
        public int spawned;
        public int completed;
        public bool active;

        public void Dispose() { }
    }

    protected override void Simulate(ref DriverState state, float delta)
    {
        if (!probePrefab || state.completed >= TotalLifetimes)
            return;

        state.phaseTicks++;

        if (state.active)
        {
            if (state.phaseTicks < LifetimeTicks)
                return;

            hierarchy.Delete((PredictedObjectID)state.activeId);
            state.active = false;
            state.phaseTicks = 0;
            state.completed++;
            return;
        }

        if (state.phaseTicks < GapTicks || state.spawned >= TotalLifetimes)
            return;

        var created = hierarchy.Create(probePrefab, new Vector3(60f, 4f, 0f), Quaternion.identity, owner);
        if (!created.HasValue)
            return;

        state.activeId = created.Value.instanceId.value;
        state.active = true;
        state.phaseTicks = 0;
        state.spawned++;
    }
}

public sealed class SoftCorrectionPoolProbe : PredictedTransform
{
    private const float InjectedOffset = 1f;
    private const float StaleMovementThreshold = 0.0001f;

    public static int priorLifetimeReuses { get; private set; }
    public static int staleCorrectionReuses { get; private set; }
    public static int verifiedCorrections { get; private set; }

    private bool _completedPooledLifetime;
    private bool _reusedAfterCompletedLifetime;
    private bool _checkedFirstLiveTick;
    private Vector3 _spawnPosition;

    public static void ResetStats()
    {
        priorLifetimeReuses = 0;
        staleCorrectionReuses = 0;
        verifiedCorrections = 0;
    }

    public override void ResetState()
    {
        base.ResetState();
        _reusedAfterCompletedLifetime = false;
        _checkedFirstLiveTick = false;
        _spawnPosition = default;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        _spawnPosition = currentState.unityPosition;
        _checkedFirstLiveTick = false;
    }

    protected override void OnAddedToPool()
    {
        base.OnAddedToPool();
        _completedPooledLifetime = true;
    }

    protected override void OnRemovedFromPool()
    {
        base.OnRemovedFromPool();
        _reusedAfterCompletedLifetime = _completedPooledLifetime;
        if (_reusedAfterCompletedLifetime)
            priorLifetimeReuses++;
    }

    protected override void OnVerifiedStateReceived(
        ulong tick,
        in PredictedTransformState predicted,
        in PredictedTransformState verified)
    {
        base.OnVerifiedStateReceived(tick, in predicted, in verified);
        if (predictionManager && !predictionManager.cachedIsServer)
            verifiedCorrections++;
    }

    protected override void Simulate(ref PredictedTransformState state, float delta)
    {
        bool liveClient = predictionManager &&
                          !predictionManager.cachedIsServer &&
                          !predictionManager.isReplaying &&
                          !predictionManager.isVerified;

        var beforeCorrection = state.unityPosition;
        base.Simulate(ref state, delta);

        if (liveClient && !_checkedFirstLiveTick)
        {
            if (_reusedAfterCompletedLifetime &&
                (state.unityPosition - beforeCorrection).sqrMagnitude > StaleMovementThreshold * StaleMovementThreshold)
            {
                staleCorrectionReuses++;
            }

            _checkedFirstLiveTick = true;
        }

        if (!liveClient)
            return;

        state.unityPosition = _spawnPosition + Vector3.right * InjectedOffset;
        transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
    }
}

/// <summary>
/// Assigns SoftCorrection to a regular state identity. The client injects a one-time state
/// error and expects normal authoritative convergence; the current base implementation stores
/// verified snapshots but never applies them to the live state.
/// </summary>
public sealed class GenericSoftCorrectionScenario : Scenario
{
    private const float TimeoutSeconds = 60f;
    private const float SettleSeconds = 3f;
    private const float AllowedTimelineLead = 30f;

    private GameObject _probePrefab;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = PredictionTestUtils.CreatePrefab<GenericSoftCorrectionProbe>(
            nameof(GenericSoftCorrectionProbe));
        _probePrefab.GetComponent<GenericSoftCorrectionProbe>().configuredPredictionPolicy =
            PredictionPolicy.SoftCorrection;
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        GenericSoftCorrectionProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"never reached scheduled start tick {_startTick}");
        }

        if (!ctx.predictionManager.hierarchy.Create(_probePrefab).HasValue)
            return ScenarioResult.Fail("failed to create generic soft-correction probe");

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => GenericSoftCorrectionProbe.injectionApplied &&
                      GenericSoftCorrectionProbe.instances.Count > 0 &&
                      GenericSoftCorrectionProbe.instances[0].verifiedState.HasValue,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"generic probe did not receive usable state: injected={GenericSoftCorrectionProbe.injectionApplied}, " +
                $"instances={GenericSoftCorrectionProbe.instances.Count}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        var probe = GenericSoftCorrectionProbe.instances[0];
        var verified = probe.verifiedState;
        if (!verified.HasValue)
            return ScenarioResult.Fail("generic probe lost its verified state");

        float divergence = Mathf.Abs(probe.currentState.value - verified.Value.value);
        if (divergence > AllowedTimelineLead)
        {
            return ScenarioResult.Fail(
                $"generic SoftCorrection state never converged: divergence={divergence:F1}, " +
                $"maxInjected={GenericSoftCorrectionProbe.maxObservedDivergence:F1}, policy={probe.predictionPolicy}");
        }

        return ScenarioResult.Ok();
    }
}

public sealed class GenericSoftCorrectionProbe : PredictedIdentity<GenericSoftCorrectionProbe.ProbeState>
{
    private const float ClientOnlyError = 100f;
    private const int InjectAfterLiveTicks = 12;

    public static readonly List<GenericSoftCorrectionProbe> instances = new();
    public static bool injectionApplied { get; private set; }
    public static float maxObservedDivergence { get; private set; }

    private int _liveTicks;
    private bool _injectedThisLifetime;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public float value;

        public void Dispose() { }
    }

    public static void ResetStats()
    {
        instances.Clear();
        injectionApplied = false;
        maxObservedDivergence = 0f;
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        state.value += 1f;

        if (!predictionManager || predictionManager.cachedIsServer ||
            predictionManager.isReplaying || predictionManager.isVerified)
        {
            return;
        }

        _liveTicks++;
        if (_injectedThisLifetime || _liveTicks < InjectAfterLiveTicks)
            return;

        state.value += ClientOnlyError;
        _injectedThisLifetime = true;
        injectionApplied = true;
        maxObservedDivergence = Mathf.Max(maxObservedDivergence, ClientOnlyError);
    }
}

/// <summary>
/// Switches a dynamic body from FullPrediction to SoftCorrection from inside a replayed tick.
/// The next physics pass must see the body frozen; otherwise replay physics advances live state.
/// </summary>
public sealed class ReplayPolicyTransitionScenario : Scenario
{
    private const float TimeoutSeconds = 60f;

    private GameObject _probePrefab;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = new GameObject(nameof(ReplayPolicyTransitionProbe));
        _probePrefab.SetActive(false);
        var body = _probePrefab.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.constraints = RigidbodyConstraints.None;
        _probePrefab.AddComponent<PredictedTransform>();
        var probe = _probePrefab.AddComponent<ReplayPolicyTransitionProbe>();
        probe.configuredPredictionPolicy = PredictionPolicy.FullPrediction;
        UnityEngine.Object.DontDestroyOnLoad(_probePrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        ReplayPolicyTransitionProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"never reached scheduled start tick {_startTick}");
        }

        if (!ctx.predictionManager.hierarchy.Create(
                _probePrefab,
                new Vector3(80f, 4f, 0f),
                Quaternion.identity).HasValue)
        {
            return ScenarioResult.Fail("failed to create replay policy-transition probe");
        }

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ReplayPolicyTransitionProbe.transitionsDuringReplay > 0 &&
                      ReplayPolicyTransitionProbe.replayPhysicsChecks > 0,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"no replay transition was observed: transitions={ReplayPolicyTransitionProbe.transitionsDuringReplay}, " +
                $"physicsChecks={ReplayPolicyTransitionProbe.replayPhysicsChecks}");
        }

        if (ReplayPolicyTransitionProbe.unfrozenReplayPhysicsPasses != 0)
        {
            return ScenarioResult.Fail(
                $"body entered SoftCorrection during replay but remained unfrozen for " +
                $"{ReplayPolicyTransitionProbe.unfrozenReplayPhysicsPasses} physics pass(es)");
        }

        return ScenarioResult.Ok();
    }
}

public sealed class ReplayPolicyTransitionProbe : PredictedRigidbody
{
    public static int transitionsDuringReplay { get; private set; }
    public static int replayPhysicsChecks { get; private set; }
    public static int unfrozenReplayPhysicsPasses { get; private set; }

    private bool _pendingReplayPhysicsCheck;

    public static void ResetStats()
    {
        transitionsDuringReplay = 0;
        replayPhysicsChecks = 0;
        unfrozenReplayPhysicsPasses = 0;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        predictionManager.onAfterPhysicsPass += OnAfterPhysicsPass;
    }

    protected override void Destroyed()
    {
        if (predictionManager)
            predictionManager.onAfterPhysicsPass -= OnAfterPhysicsPass;
        base.Destroyed();
    }

    protected override void Simulate(ref UnityRigidbodyState state, float delta)
    {
        base.Simulate(ref state, delta);

        if (!predictionManager || predictionManager.cachedIsServer ||
            !predictionManager.isReplaying || predictionPolicy != PredictionPolicy.FullPrediction)
        {
            return;
        }

        transitionsDuringReplay++;
        _pendingReplayPhysicsCheck = true;
        SetPredictionPolicy(PredictionPolicy.SoftCorrection);
    }

    private void OnAfterPhysicsPass()
    {
        if (!_pendingReplayPhysicsCheck || !predictionManager || !predictionManager.isReplaying)
            return;

        replayPhysicsChecks++;
        if (rb.constraints != RigidbodyConstraints.FreezeAll)
            unfrozenReplayPhysicsPasses++;
        _pendingReplayPhysicsCheck = false;
    }
}
