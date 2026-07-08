using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class SoftCorrectionScenario : Scenario
{
    [SerializeField] private PolicyBallRig _rig;
    [SerializeField] private float _minDivergence = 0.3f;
    [SerializeField] private float _convergedDistance = 0.15f;
    [SerializeField] private float _settleSeconds = 2f;
    [SerializeField] private float _timeout = 90f;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var floor = new GameObject("SoftFloor");
        var floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(12f, 1f, 12f);
        floor.transform.position = new Vector3(_rig.spawnPosition.x, -0.5f, _rig.spawnPosition.z);

        var ball = new GameObject("SoftBall");
        ball.SetActive(false);
        var rb = ball.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        ball.AddComponent<SphereCollider>();
        ball.AddComponent<PredictedTransform>();
        var predictedRb = ball.AddComponent<PredictedRigidbody>();
        predictedRb.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
        ball.AddComponent<SoftProbe>();
        DontDestroyOnLoad(ball);

        PredictionTestUtils.RegisterPrefab(ctx, ball);
        _rig.ballPrefab = ball;
        _rig.requiredPlayers = ctx.expectedConnections;
        SoftProbe.ResetCounters();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _rig.hasSpawned && SoftProbe.instances.Count > 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"soft ball never spawned: spawned={_rig.hasSpawned}");
        }

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        var probe = SoftProbe.instances[0];

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => SoftProbe.impulseApplied, _timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("client-side impulse was never applied");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => SoftProbe.maxObservedDivergence >= _minDivergence,
                20f,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"impulse produced no divergence (max={SoftProbe.maxObservedDivergence:F3}, expected >= {_minDivergence})");
        }

        double convergedSince = Time.realtimeSinceStartupAsDouble;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () =>
                {
                    var now = Time.realtimeSinceStartupAsDouble;
                    if (probe.divergence > _convergedDistance)
                    {
                        convergedSince = now;
                        return false;
                    }
                    return now - convergedSince >= _settleSeconds;
                },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"soft body never converged back to the verified pose (divergence={probe.divergence:F3})");
        }

        if (SoftProbe.replayViolations > 0)
            return ScenarioResult.Fail($"soft identity simulated {SoftProbe.replayViolations} times during replay/verified frames");

        return ScenarioResult.Ok();
    }
}
