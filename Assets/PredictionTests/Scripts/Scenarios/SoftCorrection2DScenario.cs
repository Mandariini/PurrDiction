using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class SoftCorrection2DScenario : Scenario
{
    [SerializeField] private PolicyBallRig _rig;
    [SerializeField] private float _minDivergence = 0.3f;
    [SerializeField] private float _convergedDistance = 0.15f;
    [SerializeField] private float _settleSeconds = 2f;
    [SerializeField] private float _timeout = 90f;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var floor = new GameObject("SoftFloor2D");
        var floorCollider = floor.AddComponent<BoxCollider2D>();
        floorCollider.size = new Vector2(12f, 1f);
        floor.transform.position = new Vector3(_rig.spawnPosition.x, -0.5f, 0f);

        var ball = new GameObject("SoftBall2D");
        ball.SetActive(false);
        var rb = ball.AddComponent<Rigidbody2D>();
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        ball.AddComponent<CircleCollider2D>();
        ball.AddComponent<PredictedTransform>();
        var predictedRb = ball.AddComponent<PredictedRigidbody2D>();
        predictedRb.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
        ball.AddComponent<SoftProbe2D>();
        DontDestroyOnLoad(ball);

        PredictionTestUtils.RegisterPrefab(ctx, ball);
        _rig.ballPrefab = ball;
        _rig.requiredPlayers = ctx.expectedConnections;
        SoftProbe2D.ResetCounters();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _rig.hasSpawned && SoftProbe2D.instances.Count > 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"2D soft ball never spawned: spawned={_rig.hasSpawned}");
        }

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        var probe = SoftProbe2D.instances[0];

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => SoftProbe2D.impulseApplied, _timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("client-side 2D impulse was never applied");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => SoftProbe2D.maxObservedDivergence >= _minDivergence,
                20f,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"2D impulse produced no divergence (max={SoftProbe2D.maxObservedDivergence:F3}, expected >= {_minDivergence})");
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
            return ScenarioResult.Fail($"2D soft body never converged back to the verified pose (divergence={probe.divergence:F3})");
        }

        if (SoftProbe2D.replayViolations > 0)
            return ScenarioResult.Fail($"2D soft identity simulated {SoftProbe2D.replayViolations} times during replay/verified frames");

        return ScenarioResult.Ok();
    }
}
