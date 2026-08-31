namespace PurrNet.Prediction
{
    /// <summary>
    /// A <see cref="PredictedIdentity{INPUT, STATE}"/> that also carries a local,
    /// non-predicted state alongside the replicated prediction state and input.
    /// The local state lives entirely outside the prediction pipeline: it is never rolled
    /// back, saved in history, interpolated, predicted, or serialized. It exists for
    /// view/visual data (e.g. values consumed by UpdateView) that must survive reconciliation
    /// untouched.
    /// The single exception is pooling: when the identity is returned to the pool,
    /// <see cref="OnAddedToPool"/> resets <see cref="local"/> to default so a future pooled
    /// reuse does not leak stale values. All other lifecycle events leave it untouched.
    /// </summary>
    /// <typeparam name="INPUT">The replicated, predicted input.</typeparam>
    /// <typeparam name="STATE">The replicated, predicted state.</typeparam>
    /// <typeparam name="LOCAL">The local, non-predicted state. Use a struct for inline
    /// value semantics or a class for reference semantics; classes are nulled on return to
    /// the pool and must be assigned by the implementor.</typeparam>
    public abstract class PredictedIdentityWithInputAndLocal<INPUT, STATE, LOCAL> :
        PredictedIdentity<INPUT, STATE>
        where STATE : struct, IPredictedData<STATE>
        where INPUT : struct, IPredictedData
    {
        /// <summary>
        /// Local, non-predicted state. The prediction pipeline never reads, writes, or resets
        /// this field; it is entirely owned by the implementor.
        /// The only exception is <see cref="OnAddedToPool"/>, which clears it to default when
        /// the identity returns to the pool.
        /// </summary>
        public LOCAL local;

        /// <summary>
        /// Shown in the inspector next to the predicted state box, together with the input.
        /// Displays the local state; override <see cref="object.ToString"/> on the LOCAL type
        /// for readable output.
        /// </summary>
        public override string GetExtraString()
        {
            return $"{base.GetExtraString()}\nLocal:\n{local?.ToString()}";
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
