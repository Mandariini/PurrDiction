using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class PolicyBallRig : DeterministicIdentity<PolicyBallRig.RigState>
{
    [SerializeField] private sfloat _spawnDelay = 4f;
    [SerializeField] private Vector3 _spawnPosition = new(20f, 5f, 0f);
    [SerializeField] private bool _ownByFirstPlayer;

    public GameObject ballPrefab { get; set; }

    public int requiredPlayers { get; set; }

    public bool hasSpawned => currentState.spawned;

    public Vector3 spawnPosition => _spawnPosition;

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

        if (predictionManager.players.players.Count < requiredPlayers)
            return;

        state.timer -= delta;
        if (state.timer > 0)
            return;

        PlayerID? owner = null;
        if (_ownByFirstPlayer)
        {
            var players = predictionManager.players.players;
            if (players.Count == 0)
                return;
            owner = players[0];
        }

        predictionManager.hierarchy.Create(ballPrefab, _spawnPosition, Quaternion.identity, owner);
        state.spawned = true;
    }
}
