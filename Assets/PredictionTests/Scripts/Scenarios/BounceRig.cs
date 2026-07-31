using PurrNet.Prediction;
using UnityEngine;

public class BounceRig : DeterministicIdentity<BounceRig.RigState>
{
    [SerializeField] private sfloat _spawnDelay = 3f;

    public GameObject ballPrefab { get; set; }

    public bool hasSpawned => currentState.spawned;

    private ulong _startTick = ulong.MaxValue;

    public void ScheduleStart(ulong startTick)
    {
        _startTick = startTick;
    }

    public struct RigState : IPredictedData<RigState>
    {
        public sfloat timer;
        public bool spawned;

        public void Dispose() { }
    }

    protected override RigState GetInitialState()
    {
        return new RigState
        {
            timer = _spawnDelay,
            spawned = false
        };
    }

    protected override void Simulate(ref RigState state, sfloat delta)
    {
        if (state.spawned || !ballPrefab)
            return;

        if (_startTick == ulong.MaxValue || predictionManager.time.tick < _startTick)
            return;

        state.timer -= delta;
        if (state.timer > 0)
            return;

        predictionManager.hierarchy.Create(ballPrefab, new Vector3(0f, 5f, 0f), Quaternion.identity);
        state.spawned = true;
    }
}
