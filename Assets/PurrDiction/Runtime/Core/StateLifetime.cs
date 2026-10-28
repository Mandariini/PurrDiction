using Unity.Collections.LowLevel.Unsafe;

namespace PurrNet.Prediction
{
    internal static class StateLifetime
    {
        /// <summary>
        /// True while <paramref name="state"/> is still default(T): the identity was never set up, or its state was
        /// already released. Such a value never came from GetInitialState or a copy, so user Dispose() must not see it.
        /// </summary>
        public static unsafe bool IsUnset<T>(ref T state) where T : struct
        {
            T unset = default;
            return UnsafeUtility.MemCmp(UnsafeUtility.AddressOf(ref state), UnsafeUtility.AddressOf(ref unset),
                UnsafeUtility.SizeOf<T>()) == 0;
        }
    }
}
