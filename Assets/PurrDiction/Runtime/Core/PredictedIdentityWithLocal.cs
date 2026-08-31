namespace PurrNet.Prediction
{
    /// <summary>
    /// A <see cref="PredictedIdentity{STATE}"/> that carries a local, non-predicted state
    /// alongside the replicated prediction state.
    /// The local state lives entirely outside the prediction pipeline.
    /// The single exception is pooling: when the identity is returned to the pool,
    /// <see cref="OnAddedToPool"/> resets <see cref="local"/> to default so a future pooled
    /// reuse does not leak stale values. All other lifecycle events leave it untouched.
    /// </summary>
    /// <typeparam name="STATE">The replicated, predicted state.</typeparam>
    /// <typeparam name="LOCAL">The local, non-predicted state. Use a struct for inline
    /// value semantics or a class for reference semantics; classes are nulled on return to
    /// the pool and must be assigned by the implementor.</typeparam>
    public abstract class PredictedIdentityWithLocal<STATE, LOCAL> : PredictedIdentity<STATE>
        where STATE : struct, IPredictedData<STATE>
    {
        /// <summary>
        /// Local, non-predicted state. The prediction pipeline never reads, writes, or resets
        /// this field; it is entirely owned by the implementor.
        /// The only exception is <see cref="OnAddedToPool"/>, which clears it to default when
        /// the identity returns to the pool.
        /// </summary>
        public LOCAL local;

        /// <summary>
        /// Shown in the inspector next to the predicted state box. Displays the local state;
        /// override <see cref="object.ToString"/> on the LOCAL type for readable output.
        /// </summary>
        public override string GetExtraString()
        {
            return $"Local:\n{local?.ToString()}";
        }

        /// <summary>
        /// Resets the local state when the identity is returned to the pool. This is the only
        /// lifecycle event that touches <see cref="local"/>. Override to customize pool-return
        /// cleanup; call base to clear the local state.
        /// </summary>
        protected override void OnAddedToPool()
        {
            base.OnAddedToPool();
            local = default;
        }
    }
}
