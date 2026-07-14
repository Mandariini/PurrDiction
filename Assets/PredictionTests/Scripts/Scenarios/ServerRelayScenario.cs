using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class ServerRelayScenario : Scenario
{
    [SerializeField] private PolicyBallRig _rig;
    [SerializeField] private float _restSeconds = 3f;
    [SerializeField] private int _minVerifiedTicks = 100;
    [SerializeField] private float _timeout = 90f;

    private const int DigestChannel = 1000;

    private int _prefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var floor = new GameObject("RelayFloor");
        var floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(12f, 1f, 12f);
        floor.transform.position = new Vector3(_rig.spawnPosition.x, -0.5f, _rig.spawnPosition.z);

        var ball = new GameObject("RelayBall");
        ball.SetActive(false);
        var rb = ball.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        ball.AddComponent<SphereCollider>();
        ball.AddComponent<PredictedTransform>();
        var predictedRb = ball.AddComponent<PredictedRigidbody>();
        predictedRb.configuredPredictionPolicy = PredictionPolicy.ServerRelay;
        ball.AddComponent<RelayProbe>();
        _prefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, ball);
        _rig.ballPrefab = ball;
        RelayProbe.ResetCounters();
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _rig.ScheduleStart(startTick);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _rig.hasSpawned && RelayProbe.instances.Count > 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"relay ball never spawned: spawned={_rig.hasSpawned}");
        }

        var probe = RelayProbe.instances[0];
        var lastPosition = probe.currentPosition;
        double lastMove = Time.realtimeSinceStartupAsDouble;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () =>
                {
                    var now = Time.realtimeSinceStartupAsDouble;
                    var position = probe.currentPosition;
                    if ((position - lastPosition).sqrMagnitude > 0.0001f)
                    {
                        lastPosition = position;
                        lastMove = now;
                    }
                    bool hasVerifiedTickEvidence = ctx.role != NetworkRole.Client ||
                                                   RelayProbe.verifiedSimulations >= _minVerifiedTicks;
                    return now - lastMove >= _restSeconds && hasVerifiedTickEvidence;
                },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"relay ball never settled: pos={probe.currentPosition}");
        }

        if (ctx.role == NetworkRole.Client)
        {
            if (!probe.isKinematicBody)
                return ScenarioResult.Fail("relay body is not kinematic on the client");

            if (RelayProbe.unverifiedSimulations > 0)
                return ScenarioResult.Fail($"relay identity simulated {RelayProbe.unverifiedSimulations} unverified ticks");

            if (RelayProbe.monotonicityViolations > 0)
                return ScenarioResult.Fail($"relay identity simulated verified ticks out of order ({RelayProbe.monotonicityViolations} violations)");

            if (RelayProbe.verifiedSimulations < _minVerifiedTicks)
                return ScenarioResult.Fail($"relay identity barely simulated ({RelayProbe.verifiedSimulations} verified ticks)");
        }

        var rest = probe.currentPosition;
        var digest = $"count={PredictionTestUtils.CountInstances(ctx.predictionManager, _prefabId)};" +
                     $"pos={Quantize(rest.x):F1},{Quantize(rest.y):F1},{Quantize(rest.z):F1}";

        return await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
    }

    private static float Quantize(float value)
    {
        value = Mathf.Round(value * 10f) * 0.1f;
        return value == 0f ? 0f : value;
    }
}
