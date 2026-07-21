using PurrNet.Prediction;
using UnityEngine;

public class BenchMover : PredictedIdentity<BenchMover.MoverInput, BenchMover.MoverState>
{
    public struct MoverInput : IPredictedData
    {
        public float x;
        public float z;
        public bool boost;

        public void Dispose() { }
    }

    public struct MoverState : IPredictedData<MoverState>
    {
        public Vector3 position;
        public Vector3 velocity;
        public uint ticks;
        public long checksum;

        public void Dispose() { }
    }

    protected override void GetFinalInput(ref MoverInput input)
    {
        var t = predictionManager.localTick;
        var phase = (t + (ulong)(id.objectId.instanceId.value % 64)) * 0.1f;
        input.x = Mathf.Sin(phase);
        input.z = Mathf.Cos(phase);
        input.boost = (t & 8) != 0;
    }

    protected override void Simulate(MoverInput input, ref MoverState state, float delta)
    {
        var speed = input.boost ? 6f : 3f;
        state.velocity = new Vector3(input.x, 0f, input.z) * speed;
        state.position += state.velocity * delta;
        state.ticks += 1;
        state.checksum += (long)(state.position.x * 1000f) ^ state.ticks;
    }
}
