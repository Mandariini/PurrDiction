using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Sits on a ServerRelay rigidbody and records, on pure clients, every Simulate call.
/// Relay identities must only ever simulate verified ticks, and the verified timeline must be
/// monotonic. Re-executing the SAME tick is legitimate: in-place frames re-deliver fresh server
/// state under an unchanged verified tick, and the reconcile re-simulates it.
/// </summary>
public class RelayProbe : PredictedIdentity<RelayProbe.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    public static readonly List<RelayProbe> instances = new();

    public static int unverifiedSimulations { get; private set; }
    public static int monotonicityViolations { get; private set; }
    public static int verifiedSimulations { get; private set; }

    private static ulong _lastVerifiedSimTick;

    public static void ResetCounters()
    {
        unverifiedSimulations = 0;
        monotonicityViolations = 0;
        verifiedSimulations = 0;
        _lastVerifiedSimTick = 0;
    }

    private PredictedTransform _predictedTransform;
    private Rigidbody _body;

    public Vector3 currentPosition => _predictedTransform.currentState.unityPosition;

    public bool isKinematicBody => _body.isKinematic;

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.ServerRelay;

    private void Awake()
    {
        _predictedTransform = GetComponent<PredictedTransform>();
        _body = GetComponent<Rigidbody>();
    }

    protected override void LateAwake()
    {
        instances.Add(this);
        _lastVerifiedSimTick = 0;
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        if (predictionManager.cachedIsServer)
            return;

        if (!predictionManager.isVerified)
        {
            unverifiedSimulations++;
            return;
        }

        if (!predictionManager.isVerifiedView)
            return;

        verifiedSimulations++;
        var tick = predictionManager.localTickInContext;
        if (tick < _lastVerifiedSimTick)
            monotonicityViolations++;
        else
            _lastVerifiedSimTick = tick;
    }
}
