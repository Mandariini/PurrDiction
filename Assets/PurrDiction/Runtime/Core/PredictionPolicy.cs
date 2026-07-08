using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Tuning for <see cref="PredictionPolicy.SoftCorrection"/> pose blending.
    /// </summary>
    [Serializable]
    public struct SoftCorrectionSettings
    {
        [Tooltip("Fraction of the remaining error corrected per second (exponential decay rate).")]
        public float correctionRate;

        [Tooltip("Position error magnitude above which the remaining correction is applied instantly.")]
        public float snapPositionThreshold;

        [Tooltip("Rotation error in degrees above which the remaining correction is applied instantly.")]
        public float snapRotationThreshold;

        public static SoftCorrectionSettings Default => new()
        {
            correctionRate = 8f,
            snapPositionThreshold = 3f,
            snapRotationThreshold = 75f
        };
    }

    /// <summary>
    /// Controls how a predicted identity participates in client-side prediction and reconciliation.
    /// Policies only alter behavior on clients; the server always simulates every identity normally.
    /// Deterministic identities can use FullPrediction, ServerRelay, and PredictedIfOwned without
    /// sending per-tick state; SoftCorrection requires authoritative state deltas and is unavailable.
    /// </summary>
    public enum PredictionPolicy : byte
    {
        /// <summary>
        /// Default behavior: simulated every local tick, rolled back and resimulated on every reconcile.
        /// </summary>
        FullPrediction = 0,

        /// <summary>
        /// The identity only executes verified (server-confirmed) ticks on clients.
        /// It is never predicted into the local future and never resimulated during replays,
        /// so it lives on the verified timeline (roughly RTT + buffering behind the local player).
        /// Rigidbodies with this policy are kept kinematic on clients and posed from verified state,
        /// acting as static geometry for locally predicted objects.
        /// </summary>
        ServerRelay = 1,

        /// <summary>
        /// The identity simulates locally every tick but is excluded from rollback and resimulation;
        /// its rigidbody is frozen while replays run. Verified server state is used as a correction
        /// target instead of a restore point: divergence is measured against the client's own history
        /// at the verified tick and blended back into the live simulation over time.
        /// Client state is convergent rather than authoritative — intended for physics objects whose
        /// exact pose is not gameplay-critical (debris, props, ragdolls).
        /// </summary>
        SoftCorrection = 2,

        /// <summary>
        /// Resolves per client to <see cref="FullPrediction"/> while the local player owns the identity
        /// and <see cref="ServerRelay"/> otherwise (including unowned). The common "predict only what I
        /// control, relay everyone else" setup: your own pawn/vehicle predicts and reconciles normally,
        /// while remote-owned copies just play verified server state. Ownership changes flip the mode
        /// automatically at the next tick.
        /// </summary>
        PredictedIfOwned = 3
    }
}
