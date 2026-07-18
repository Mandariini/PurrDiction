using PurrNet.Prediction;
using UnityEngine;

public class PieceProbe : PredictedIdentity<PieceProbe.ProbeState>
{
    public const uint TickCap = 150;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public uint ticksAlive;

        public void Dispose() { }
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        if (state.ticksAlive < TickCap)
            state.ticksAlive += 1;
    }
}

/// <summary>
/// Driver for the compound piece-hierarchy prefab. Two rigs pair up deterministically;
/// the target rig reparents its armC piece under the actor rig, the actor deletes its
/// own armB subtree, and finally the target deletes its whole root while the reparented
/// armC survives as a promoted orphan. All steps run inside Simulate so replays and
/// rollbacks re-execute them through the piece reconcile machinery.
/// </summary>
public class PieceRigRoot : PredictedIdentity<PieceRigRoot.RigState>
{
    public PieceProbe armA;
    public PieceProbe armB;
    public PieceProbe armC;

    public const uint ReparentDelay = 60;
    public const uint DeleteArmDelay = 100;
    public const uint DeleteRootDelay = 140;

    public struct RigState : IPredictedData<RigState>
    {
        public uint startTick;
        public byte step;
        public byte isActor;

        public void Dispose() { }
    }

    protected override void Simulate(ref RigState state, float delta)
    {
        if (state.step >= 3)
            return;

        if (state.startTick == 0)
        {
            if (!TryGetPartnerRoot(out var partnerRoot))
                return;

            state.startTick = (uint)predictionManager.time.tick;
            state.isActor = id.objectId.instanceId.value < partnerRoot.instanceId.value ? (byte)1 : (byte)0;
            return;
        }

        uint elapsed = (uint)predictionManager.time.tick - state.startTick;
        var hierarchy = predictionManager.hierarchy;

        if (state.step == 0 && elapsed >= ReparentDelay)
        {
            state.step = 1;

            if (state.isActor == 0 && armC && TryGetPartnerRoot(out var reparentTarget) &&
                hierarchy.TryGetGameObject(reparentTarget, out var targetGo))
            {
                armC.transform.SetParent(targetGo.transform, true);
            }

            return;
        }

        if (state.step == 1 && elapsed >= DeleteArmDelay)
        {
            state.step = 2;

            if (state.isActor == 1 && armB)
                hierarchy.Delete(armB.gameObject);

            return;
        }

        if (state.step == 2 && elapsed >= DeleteRootDelay)
        {
            state.step = 3;

            if (state.isActor == 0)
                hierarchy.Delete(gameObject);
        }
    }

    private bool TryGetPartnerRoot(out PredictedObjectID partnerRoot)
    {
        ref var worldState = ref predictionManager.hierarchy.currentState;
        var myRoot = id.objectId;

        int myPrefabId = int.MinValue;

        for (var i = 0; i < worldState.spawnedPrefabs.Count; i++)
        {
            var record = worldState.spawnedPrefabs[i];
            if (record.instanceId.Equals(myRoot))
            {
                myPrefabId = record.prefabId;
                break;
            }
        }

        if (myPrefabId != int.MinValue)
        {
            for (var i = 0; i < worldState.spawnedPrefabs.Count; i++)
            {
                var record = worldState.spawnedPrefabs[i];
                if (record.isRootRecord && record.prefabId == myPrefabId && !record.instanceId.Equals(myRoot))
                {
                    partnerRoot = record.instanceId;
                    return true;
                }
            }
        }

        partnerRoot = default;
        return false;
    }
}
