using PurrNet.Prediction;
using UnityEngine;

public class BenchPassiveMover : PredictedIdentity<BenchPassiveMover.MoverState>
{
    public struct MoverState : IPredictedData<MoverState>
    {
        public Vector3 position;
        public Vector3 velocity;
        public uint ticks;
        public long checksum;

        public void Dispose() { }
    }

    protected override void Simulate(ref MoverState state, float delta)
    {
        var phase = (state.ticks + (uint)(id.objectId.instanceId.value % 64)) * 0.1f;
        state.velocity = new Vector3(Mathf.Sin(phase), 0f, Mathf.Cos(phase)) * 3f;
        state.position += state.velocity * delta;
        state.ticks += 1;
        state.checksum += (long)(state.position.x * 1000f) ^ state.ticks;
    }
}
