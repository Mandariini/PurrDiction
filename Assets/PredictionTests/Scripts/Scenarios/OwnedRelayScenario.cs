using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class OwnedRelayScenario : Scenario
{
    [SerializeField] private PolicyBallRig _rig;
    [SerializeField] private float _restSeconds = 3f;
    [SerializeField] private float _timeout = 90f;

    private const int DigestChannel = 1100;

    private int _prefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var floor = new GameObject("OwnedRelayFloor");
        var floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(12f, 1f, 12f);
        floor.transform.position = new Vector3(_rig.spawnPosition.x, -0.5f, _rig.spawnPosition.z);

        var ball = new GameObject("OwnedRelayBall");
        ball.SetActive(false);
        var rb = ball.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        ball.AddComponent<SphereCollider>();
        ball.AddComponent<PredictedTransform>();
        var predictedRb = ball.AddComponent<PredictedRigidbody>();
        predictedRb.configuredPredictionPolicy = PredictionPolicy.PredictedIfOwned;
        ball.AddComponent<OwnedRelayProbe>();
        _prefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, ball);
        _rig.ballPrefab = ball;
        _rig.requiredPlayers = ctx.expectedConnections;
        OwnedRelayProbe.ResetCounters();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _rig.hasSpawned && OwnedRelayProbe.instances.Count > 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"owned/relay ball never spawned: spawned={_rig.hasSpawned}");
        }

        var probe = OwnedRelayProbe.instances[0];
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
                    return now - lastMove >= _restSeconds;
                },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"owned/relay ball never settled: pos={probe.currentPosition}");
        }

        if (ctx.role == NetworkRole.Client)
        {
            bool owns = probe.IsOwner();
            if (owns)
            {
                if (OwnedRelayProbe.livePredictedTicks < 50)
                    return ScenarioResult.Fail($"owner did not predict live ticks ({OwnedRelayProbe.livePredictedTicks})");
                if (probe.isKinematicBody)
                    return ScenarioResult.Fail("owned body is kinematic; should be dynamically predicted");
            }
            else
            {
                if (OwnedRelayProbe.unverifiedRelayTicks > 0)
                    return ScenarioResult.Fail($"non-owner simulated {OwnedRelayProbe.unverifiedRelayTicks} unverified ticks; should relay only");
                if (!probe.isKinematicBody)
                    return ScenarioResult.Fail("relayed body is not kinematic on the non-owning client");
            }
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
