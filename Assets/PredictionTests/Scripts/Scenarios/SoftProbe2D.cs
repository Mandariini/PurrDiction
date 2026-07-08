using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// 2D counterpart of SoftProbe: injects a client-only velocity impulse on a SoftCorrection
/// Rigidbody2D and records violations if the identity ever simulates during a replay or
/// verified frame. Exists so the 2D freeze/correction code path has CI coverage of its own.
/// </summary>
public class SoftProbe2D : PredictedIdentity<SoftProbe2D.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    public static readonly List<SoftProbe2D> instances = new();

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
    [SerializeField] private Vector2 _impulse = new(2f, 5f);

    private PredictedTransform _predictedTransform;
    private PredictedRigidbody2D _predictedRigidbody;
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
        _predictedRigidbody = GetComponent<PredictedRigidbody2D>();
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
