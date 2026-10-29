namespace PurrNet.Prediction
{
    /// <summary>
    /// Receives the typed data of a <see cref="PredictedIdentity"/> without boxing.
    /// See <see cref="PredictedIdentity.VisitData"/>.
    /// </summary>
    public interface IPredictedDataVisitor
    {
        /// <summary>The identity's current state.</summary>
        void VisitState<T>(PredictedIdentity identity, ref T state) where T : struct;

        /// <summary>The identity's current input. Only called for identities with input.</summary>
        void VisitInput<T>(PredictedIdentity identity, ref T input) where T : struct;
    }
}
