namespace PurrNet.Prediction
{
    /// <summary>
    /// A <see cref="PredictedModule{TState}"/> that carries a local, non-predicted state
    /// alongside the replicated module state.
    /// The local state lives entirely outside the prediction pipeline: it is never rolled
    /// back, saved in history, interpolated, predicted, or serialized. It exists for
    /// view/visual data that must survive reconciliation untouched.
    /// The single exception is pooling: when the owning identity is parked in a prefab pool
    /// but this module instance is kept for reuse, <see cref="ReleaseStateForPool"/> resets
    /// <see cref="local"/> to default so a future pooled reuse does not leak stale values.
    /// All other lifecycle events leave it untouched.
    /// </summary>
    /// <typeparam name="TState">The replicated, predicted module state.</typeparam>
    /// <typeparam name="LOCAL">The local, non-predicted state. Use a struct for inline
    /// value semantics or a class for reference semantics; classes are nulled on return to
    /// the pool and must be assigned by the implementor.</typeparam>
    public abstract class PredictedModuleWithLocal<TState, LOCAL> : PredictedModule<TState>
        where TState : struct, IPredictedData<TState>
    {
        public PredictedModuleWithLocal(PredictedIdentity identity) : base(identity) { }

        /// <summary>
        /// Local, non-predicted state. The prediction pipeline never reads, writes, or resets
        /// this field; it is entirely owned by the implementor.
        /// The only exception is <see cref="ReleaseStateForPool"/>, which clears it to default
        /// when the owning identity returns to the pool.
        /// </summary>
        public LOCAL local;

        /// <summary>
        /// Resets the local state when the owning identity is returned to the pool. This is the
        /// only lifecycle event that touches <see cref="local"/>. Override to customize
        /// pool-return cleanup; call base to clear the local state.
        /// </summary>
        protected override void ReleaseStateForPool()
        {
            base.ReleaseStateForPool();
            local = default;
        }

        /// <summary>
        /// Shown in the inspector as the module content box. Displays the replicated state
        /// together with the local state; override <see cref="object.ToString"/> on the LOCAL
        /// type for readable output.
        /// </summary>
        public override string ToString()
        {
            return $"{base.ToString()}\nLocal:\n{local?.ToString()}";
        }
    }
}
