using PurrNet.Prediction;

public class DeterministicGauntlet : DeterministicIdentity<DeterministicGauntlet.GauntletInput, DeterministicGauntlet.GauntletState>
{
    public const int DurationTicks = 200;

    private ulong _startTick = ulong.MaxValue;

    public void ScheduleStart(ulong startTick)
    {
        _startTick = startTick;
    }

    public struct GauntletInput : IPredictedData
    {
        public int step;

        public void Dispose() { }
    }

    public struct GauntletState : IPredictedData<GauntletState>
    {
        public PredictedRandom rng;
        public ulong rngAccum;
        public ulong inputSum;
        public uint steps;

        public void Dispose() { }
    }

    public bool isDone => currentState.steps >= DurationTicks;

    public string StateDigest()
    {
        ref var state = ref currentState;
        return $"steps={state.steps};seed={state.rng.seed};acc={state.rngAccum};inputs={state.inputSum}";
    }

    protected override GauntletState GetInitialState()
    {
        return new GauntletState
        {
            rng = PredictedRandom.Create(0xC0FFEE)
        };
    }

    protected override void GetFinalInput(ref GauntletInput input)
    {
        bool active = _startTick != ulong.MaxValue &&
                      predictionManager.time.tick >= _startTick &&
                      currentState.steps < DurationTicks;

        input.step = active ? 1 + (int)(predictionManager.localTick % 7) : 0;
    }

    protected override void Simulate(GauntletInput input, ref GauntletState state, sfloat delta)
    {
        if (_startTick == ulong.MaxValue || predictionManager.time.tick < _startTick)
            return;

        if (state.steps >= DurationTicks)
            return;

        state.steps += 1;
        state.rngAccum += (ulong)state.rng.Next(1000);
        state.inputSum += (ulong)input.step;
    }
}
