using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

public class InputBandwidthPawn : PredictedIdentity<InputBandwidthPawn.NoiseInput, InputBandwidthPawn.NoiseState>
{
    public enum NoiseMode
    {
        Constant,
        Smooth,
        Full
    }

    public static NoiseMode noiseMode = NoiseMode.Full;

    public struct NoiseInput : IPredictedData
    {
        public Vector3 v1;
        public Vector3 v2;
        public NormalizedFloat horizontal;
        public NormalizedFloat vertical;
        public bool jump;
        public bool dash;

        public void Dispose() { }

        public override string ToString()
        {
            return $"v1: {v1} v2: {v2} h: {horizontal} v: {vertical}";
        }
    }

    public struct NoiseState : IPredictedData<NoiseState>
    {
        public float accumulator;
        public long ticks;

        public void Dispose() { }

        public override string ToString()
        {
            return $"accumulator: {accumulator} ticks: {ticks}";
        }
    }

    protected override void GetFinalInput(ref NoiseInput input)
    {
        FillNoise(ref input, predictionManager.localTick, id.GetHashCode());
    }

    public static void FillNoise(ref NoiseInput input, ulong tick, int idHash)
    {
        switch (noiseMode)
        {
            case NoiseMode.Constant:
                input.v1 = new Vector3(0.25f, 0.5f, 0.75f);
                input.v2 = new Vector3(-0.25f, -0.5f, -0.75f);
                input.horizontal = new NormalizedFloat(1f);
                input.vertical = new NormalizedFloat(-1f);
                break;
            case NoiseMode.Smooth:
                float angle = tick * 0.05f;
                input.v1 = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                input.v2 = new Vector3(0f, Mathf.Cos(angle * 0.7f), Mathf.Sin(angle * 0.7f));
                input.horizontal = new NormalizedFloat(Mathf.Cos(angle));
                input.vertical = new NormalizedFloat(Mathf.Sin(angle));
                break;
            case NoiseMode.Full:
                ulong seed = Mix(tick ^ ((ulong)idHash << 20));
                input.v1 = RandomUnitVector(ref seed);
                input.v2 = RandomUnitVector(ref seed);
                input.horizontal = new NormalizedFloat(RandomFloat(ref seed) * 2f - 1f);
                input.vertical = new NormalizedFloat(RandomFloat(ref seed) * 2f - 1f);
                break;
        }

        input.jump = false;
        input.dash = false;
    }

    protected override void Simulate(NoiseInput input, ref NoiseState state, float delta)
    {
        state.accumulator += input.v1.x + input.v1.y + input.v1.z
                             + input.v2.x + input.v2.y + input.v2.z
                             + input.horizontal.value + input.vertical.value;
        state.ticks += 1;
    }

    static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    static float RandomFloat(ref ulong seed)
    {
        seed = Mix(seed);
        return (seed >> 40) / (float)(1UL << 24);
    }

    static Vector3 RandomUnitVector(ref ulong seed)
    {
        var v = new Vector3(
            RandomFloat(ref seed) * 2f - 1f,
            RandomFloat(ref seed) * 2f - 1f,
            RandomFloat(ref seed) * 2f - 1f);
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }
}
