using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Sits on a PredictedIfOwned rigidbody. On the client that owns it the identity resolves to
/// FullPrediction (simulates live, unverified ticks); on every other client it resolves to
/// ServerRelay (kinematic, only verified ticks). The probe records which happened so the
/// scenario can assert the split.
/// </summary>
public class OwnedRelayProbe : PredictedIdentity<OwnedRelayProbe.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    public static readonly List<OwnedRelayProbe> instances = new();

    public static int livePredictedTicks { get; private set; }
    public static int unverifiedRelayTicks { get; private set; }

    public static void ResetCounters()
    {
        livePredictedTicks = 0;
        unverifiedRelayTicks = 0;
    }

    private PredictedTransform _predictedTransform;
    private Rigidbody _body;

    public Vector3 currentPosition => _predictedTransform.currentState.unityPosition;
    public bool isKinematicBody => _body.isKinematic;

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.PredictedIfOwned;

    private void Awake()
    {
        _predictedTransform = GetComponent<PredictedTransform>();
        _body = GetComponent<Rigidbody>();
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        if (predictionManager.cachedIsServer)
            return;

        // A live tick outside reconciliation: only an owned (FullPrediction) body reaches this,
        // since a relayed body simulates exclusively on verified reconcile frames.
        if (!predictionManager.isReplaying && !predictionManager.isVerified)
        {
            if (IsOwner())
                livePredictedTicks++;
            else
                unverifiedRelayTicks++;
        }
    }
}
