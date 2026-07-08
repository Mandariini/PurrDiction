using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Sits on a SoftCorrection rigidbody. On a pure client it injects a local-only velocity
/// impulse a while after spawn, creating a real divergence the correction has to absorb,
/// and records violations if the identity ever simulates during a replay or verified frame.
/// </summary>
public class SoftProbe : PredictedIdentity<SoftProbe.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    public static readonly List<SoftProbe> instances = new();

    public static int replayViolations { get; private set; }
    public static bool impulseApplied { get; private set; }
    public static float maxObservedDivergence { get; private set; }

    public static void ResetCounters()
    {
        replayViolations = 0;
        impulseApplied = false;
        maxObservedDivergence = 0f;
    }

    [SerializeField] private int _impulseAfterTicks = 90;
    [SerializeField] private Vector3 _impulse = new(2f, 4f, 1f);

    private PredictedTransform _predictedTransform;
    private PredictedRigidbody _predictedRigidbody;
    private int _liveTicks;

    /// <summary>
    /// Distance between the client's live pose and the latest verified server pose.
    /// </summary>
    public float divergence
    {
        get
        {
            var verified = _predictedTransform.verifiedState;
            if (!verified.HasValue)
                return 0f;
            return Vector3.Distance(_predictedTransform.currentState.unityPosition, verified.Value.unityPosition);
        }
    }

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.SoftCorrection;

    private void Awake()
    {
        _predictedTransform = GetComponent<PredictedTransform>();
        _predictedRigidbody = GetComponent<PredictedRigidbody>();
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

        if (predictionManager.isReplaying || predictionManager.isVerified)
        {
            replayViolations++;
            return;
        }

        if (impulseApplied)
        {
            maxObservedDivergence = Mathf.Max(maxObservedDivergence, divergence);
            return;
        }

        if (++_liveTicks < _impulseAfterTicks)
            return;

        _predictedRigidbody.linearVelocity += _impulse;
        impulseApplied = true;
    }
}
