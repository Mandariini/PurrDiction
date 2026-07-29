using PurrNet.Prediction;

public class DesyncProbe : DeterministicIdentity<DesyncProbe.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public ulong count;

        public void Dispose() { }
    }

    public ulong corruptAtCount { get; set; }
    public ulong corruptionsApplied { get; private set; }

    protected override void Simulate(ref ProbeState state, sfloat delta)
    {
        state.count += 1;
        if (corruptAtCount != 0 && state.count == corruptAtCount)
        {
            state.count += 9999;
            corruptionsApplied++;
        }
    }

    public string Digest()
    {
        return $"delta={(long)currentState.count - (long)predictionManager.time.tick}";
    }
}
