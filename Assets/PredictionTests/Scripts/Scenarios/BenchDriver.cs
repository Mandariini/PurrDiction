using PurrNet.Prediction;
using UnityEngine;

public class BenchDriver : PredictedIdentity<BenchDriver.DriverState>
{
    public const int SpawnsPerTick = 10;

    public GameObject moverPrefab;
    public GameObject passiveMoverPrefab;
    public int targetCount = 200;
    public int inputEvery = 1;

    public struct DriverState : IPredictedData<DriverState>
    {
        public int spawned;

        public void Dispose() { }
    }

    public bool doneSpawning => currentState.spawned >= targetCount;

    protected override void Simulate(ref DriverState state, float delta)
    {
        if (!moverPrefab)
            return;

        var budget = SpawnsPerTick;
        while (state.spawned < targetCount && budget-- > 0)
        {
            var lane = state.spawned % 20;
            var row = state.spawned / 20;
            var useInput = inputEvery > 0 && state.spawned % inputEvery == 0;
            var prefab = useInput || !passiveMoverPrefab ? moverPrefab : passiveMoverPrefab;
            hierarchy.Create(prefab, new Vector3(lane * 3f, 0f, row * 3f), Quaternion.identity);
            state.spawned += 1;
        }
    }
}
