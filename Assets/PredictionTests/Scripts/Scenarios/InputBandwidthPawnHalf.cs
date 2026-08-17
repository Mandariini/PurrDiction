using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

public class InputBandwidthPawnHalf : PredictedIdentity<InputBandwidthPawnHalf.HalfInput, InputBandwidthPawnHalf.HalfState>
{
    public struct HalfInput : IPredictedData
    {
        public HalfVector3 v1;
        public HalfVector3 v2;
        public NormalizedFloat horizontal;
        public NormalizedFloat vertical;
        public bool jump;
        public bool dash;

        public void Dispose() { }

        public override string ToString()
        {
            return $"v1: {(Vector3)v1} v2: {(Vector3)v2} h: {horizontal} v: {vertical}";
        }
    }

    public struct HalfState : IPredictedData<HalfState>
    {
        public float accumulator;
        public long ticks;

        public void Dispose() { }

        public override string ToString()
        {
            return $"accumulator: {accumulator} ticks: {ticks}";
        }
    }

    protected override void GetFinalInput(ref HalfInput input)
    {
        var noise = default(InputBandwidthPawn.NoiseInput);
        InputBandwidthPawn.FillNoise(ref noise, predictionManager.localTick, id.GetHashCode());
        input.v1 = noise.v1;
        input.v2 = noise.v2;
        input.horizontal = noise.horizontal;
        input.vertical = noise.vertical;
        input.jump = noise.jump;
        input.dash = noise.dash;
    }

    protected override void Simulate(HalfInput input, ref HalfState state, float delta)
    {
        Vector3 v1 = input.v1;
        Vector3 v2 = input.v2;
        state.accumulator += v1.x + v1.y + v1.z + v2.x + v2.y + v2.z
                             + input.horizontal.value + input.vertical.value;
        state.ticks += 1;
    }
}
