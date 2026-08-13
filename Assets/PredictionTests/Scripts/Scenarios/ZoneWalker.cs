using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Input-driven mover that oscillates across the smoke zone's x boundary, mimicking a player
/// walking in and out of a trigger volume. Remote peers predict its inputs and mispredict the
/// crossing timing under latency, churning the zone tracker's list through rollback. After
/// parkTick every walker steers to the zone center and stays inside so membership quiesces
/// for the cross-peer digest.
/// </summary>
public class ZoneWalker : PredictedIdentity<ZoneWalker.WalkerInput, ZoneWalker.WalkerState>
{
    public static ulong parkTick;
    public static uint halfPeriodTicks = 30;
    public static float baseX;

    public struct WalkerInput : IPredictedData
    {
        public float targetX;

        public void Dispose() { }
    }

    public struct WalkerState : IPredictedData<WalkerState>
    {
        public void Dispose() { }
    }

    private Rigidbody _rigidbody;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
    }

    protected override void GetFinalInput(ref WalkerInput input)
    {
        var tick = predictionManager.localTick;

        if (parkTick != 0 && tick >= parkTick)
        {
            input.targetX = 0f;
            return;
        }

        input.targetX = (tick / halfPeriodTicks) % 2 == 0 ? -6f : 6f;
    }

    protected override void Simulate(WalkerInput input, ref WalkerState state, float delta)
    {
        if (!_rigidbody)
            return;

        float targetXWorld = baseX + input.targetX;
        float vx = Mathf.Clamp((targetXWorld - _rigidbody.position.x) * 4f, -8f, 8f);
        var velocity = _rigidbody.linearVelocity;
        _rigidbody.linearVelocity = new Vector3(vx, velocity.y, 0f);
    }
}
