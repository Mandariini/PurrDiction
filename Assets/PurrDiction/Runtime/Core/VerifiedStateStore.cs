using System;

namespace PurrNet.Prediction
{
    // Equality must be exact so full frames and deltas anchored on one tick decode the same bits.
    internal interface IAuthoritativeState<TSelf> : IDisposable where TSelf : struct
    {
        TSelf DeepCopy();
        bool HasSameContents(ref TSelf other);
    }

    // Identities and modules share baseline rules. Callers apply their own pruning policy first.
    internal static class VerifiedStateStore<T> where T : struct, IAuthoritativeState<T>
    {
        // Equal persistent states can share an anchor; event batches require their own tick.
        public static bool StoreLive(History<T> history, ulong serverTick, ref T state, bool coalesceEqualAdjacent)
        {
            if (coalesceEqualAdjacent && history.TryReadLatest(serverTick, out var latest) &&
                latest.HasSameContents(ref state))
                return false;

            history.Write(serverTick, state.DeepCopy());
            return true;
        }

        // Wire-unchanged records can reuse their baseline only if no newer value intervened.
        // Event batches always require their own tick.
        public static void StoreReceived(History<T> history, ulong serverTick, ref T state,
            ulong? unchangedBaselineTick, bool trustUnchangedBaseline)
        {
            if (trustUnchangedBaseline && unchangedBaselineTick.HasValue &&
                unchangedBaselineTick.Value <= serverTick && history.Count > 0 &&
                history.MostRecentTick <= unchangedBaselineTick.Value)
                return;

            history.Write(serverTick, state.DeepCopy());
        }
    }
}
