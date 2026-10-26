using System;
using Cysharp.Threading.Tasks;
using PurrNet.Prediction;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Local, non-networked rigidbodies (Rigidbody + LocalPhysics, Rigidbody2D + LocalPhysics2D) only
/// move when PurrDiction steps physics, once per prediction tick. With interpolation enabled on the
/// body they must still glide every rendered frame, like predicted objects do, instead of stepping
/// once per tick (report: local ragdolls and debris jitter at 20 Hz even with Interpolate).
/// </summary>
public sealed class LocalPhysicsSmoothnessScenario : Scenario
{
    private const float Speed = 4f;
    private const float WarmupSeconds = 1f;
    private const float SampleSeconds = 3f;
    private const float MaxSteppedFraction = 0.1f;

    private sealed class Track
    {
        public string name;
        public Transform transform;
        public float lastX;
        public int stepped;
        public int still;
        public float travelled;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (!ctx.isClient)
            return ScenarioResult.Ok("no view on a dedicated server");

        var world = ctx.predictionManager;
        var scene = world.gameObject.scene;

        var body3D = new GameObject("LocalPhysicsSmoothnessBody3D");
        SceneManager.MoveGameObjectToScene(body3D, scene);
        body3D.transform.position = new Vector3(0, 200, 0);
        var rb = body3D.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.sleepThreshold = 0f;
        body3D.AddComponent<LocalPhysics>();

        var body2D = new GameObject("LocalPhysicsSmoothnessBody2D");
        SceneManager.MoveGameObjectToScene(body2D, scene);
        body2D.transform.position = new Vector3(0, 300, 0);
        var rb2D = body2D.AddComponent<Rigidbody2D>();
        rb2D.gravityScale = 0f;
        rb2D.linearDamping = 0f;
        rb2D.angularDamping = 0f;
        rb2D.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb2D.sleepMode = RigidbodySleepMode2D.NeverSleep;
        body2D.AddComponent<LocalPhysics2D>();

        try
        {
            await UniTask.Yield(ctx.cancellationToken);
            rb.linearVelocity = new Vector3(Speed, 0, 0);
            rb2D.linearVelocity = new Vector2(Speed, 0);
            await UniTask.Delay(TimeSpan.FromSeconds(WarmupSeconds), cancellationToken: ctx.cancellationToken);

            await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, ctx.cancellationToken);
            var tracks = new[]
            {
                new Track { name = "3D", transform = body3D.transform, lastX = body3D.transform.position.x },
                new Track { name = "2D", transform = body2D.transform, lastX = body2D.transform.position.x }
            };

            int frames = 0;
            float elapsed = 0f;
            while (elapsed < SampleSeconds)
            {
                await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, ctx.cancellationToken);
                float dt = Time.unscaledDeltaTime;
                float expected = Speed * dt;
                elapsed += dt;
                frames++;

                foreach (var track in tracks)
                {
                    float x = track.transform.position.x;
                    float dx = x - track.lastX;
                    track.lastX = x;
                    track.travelled += dx;

                    // Stepped: less than a quarter or more than triple the distance this frame's time covers.
                    if (dx < expected * 0.25f || dx > expected * 3f)
                        track.stepped++;
                    if (Mathf.Abs(dx) < 1e-6f)
                        track.still++;
                }
            }

            if (frames < world.tickRate * SampleSeconds * 1.5f)
                return ScenarioResult.Ok($"too few frames per tick to judge smoothness ({frames} frames)");

            string failure = null, details = $"frames={frames} fps={frames / elapsed:F0} tick={world.tickRate}Hz";
            foreach (var track in tracks)
            {
                float stepped = track.stepped / (float)frames;
                var detail = $"{track.name}: stepped={stepped:P1} still={track.still} travelled={track.travelled:F2}/{Speed * elapsed:F2}";
                details += " | " + detail;
                if (stepped > MaxSteppedFraction)
                    failure ??= $"local {track.name} rigidbody stepped instead of gliding";
            }

            return failure == null ? ScenarioResult.Ok(details) : ScenarioResult.Fail($"{failure}: {details}");
        }
        finally
        {
            Destroy(body3D);
            Destroy(body2D);
        }
    }
}
