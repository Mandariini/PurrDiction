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
        [Min(0f)]
        public float correctionRate;

        [Tooltip("Position error magnitude above which the remaining correction is applied instantly.")]
        [Min(0f)]
        public float snapPositionThreshold;

        [Tooltip("Rotation error in degrees above which the remaining correction is applied instantly.")]
        [Min(0f)]
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
    /// sending per-tick simulation state; ownership metadata is synchronized when it changes.
    /// SoftCorrection uses authoritative state deltas when a non-deterministic identity explicitly
    /// implements verified-state correction through supportsSoftCorrection. Policies requesting
    /// soft correction automatically use ServerRelay for unsupported identities.
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
        /// Identities that do not support verified-state correction behave as ServerRelay while
        /// retaining SoftCorrection as their configured policy.
        /// </summary>
        SoftCorrection = 2,

        /// <summary>
        /// Resolves per client to <see cref="FullPrediction"/> while the local player owns the identity
        /// and <see cref="ServerRelay"/> otherwise (including unowned). The common "predict only what I
        /// control, relay everyone else" setup: your own pawn/vehicle predicts and reconciles normally,
        /// while remote-owned copies just play verified server state. Ownership changes flip the mode
        /// automatically when the ownership change is received in the next verified frame.
        /// </summary>
        PredictedIfOwned = 3,

        /// <summary>
        /// Resolves per client to <see cref="FullPrediction"/> while the local player owns the
        /// identity and <see cref="SoftCorrection"/> otherwise (including unowned). This keeps
        /// locally controlled objects fully predicted while remote copies continue simulating
        /// locally and converge toward verified server state without rollback. Only available to
        /// non-deterministic identities that support soft correction; the non-owner branch uses
        /// <see cref="ServerRelay"/> automatically for identities that do not support it.
        /// </summary>
        PredictedIfOwnedWithSoftFallback = 4
    }
}
