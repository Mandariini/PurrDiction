using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction.Profiler
{
    public static class TickBandwidthProfiler
    {
        static List<PackingInfo> _wroteStates, _readStates, _wroteInputs, _readInputs;
        static int _dispatchDepth;

        /// <summary>
        /// Collects samples without a tick-ended subscriber. Reading a sample list outside
        /// that callback enables polling capture; set this false when polling ends.
        /// The profiler window subscribes automatically and needs no manual configuration.
        /// </summary>
        public static bool collectWithoutSubscribers { get; set; }

        public static IReadOnlyList<PackingInfo> wroteStates => ReadSamples(ref _wroteStates);
        public static IReadOnlyList<PackingInfo> readStates => ReadSamples(ref _readStates);
        public static IReadOnlyList<PackingInfo> wroteInputs => ReadSamples(ref _wroteInputs);
        public static IReadOnlyList<PackingInfo> readInputs => ReadSamples(ref _readInputs);

        public static event Action onTickEnded;

        static IReadOnlyList<PackingInfo> ReadSamples(ref List<PackingInfo> samples)
        {
            if (_dispatchDepth == 0)
                collectWithoutSubscribers = true;
            return samples ??= new List<PackingInfo>(256);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool ShouldCollect() => collectWithoutSubscribers || onTickEnded != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnWroteState(Type parent, int bitCount, UnityEngine.Object reference)
        {
            if (!ShouldCollect()) return;
            (_wroteStates ??= new List<PackingInfo>(256)).Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnReadState(Type parent, int bitCount, UnityEngine.Object reference)
        {
            if (!ShouldCollect()) return;
            (_readStates ??= new List<PackingInfo>(256)).Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnWroteInput(Type parent, int bitCount, UnityEngine.Object reference)
        {
            if (!ShouldCollect()) return;
            (_wroteInputs ??= new List<PackingInfo>(256)).Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnReadInput(Type parent, int bitCount, UnityEngine.Object reference)
        {
            if (!ShouldCollect()) return;
            (_readInputs ??= new List<PackingInfo>(256)).Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        public static void MarkEndOfTick()
        {
            _dispatchDepth++;
            try
            {
                onTickEnded?.Invoke();
            }
            finally
            {
                _dispatchDepth--;
                _wroteStates?.Clear();
                _readStates?.Clear();
                _wroteInputs?.Clear();
                _readInputs?.Clear();
            }
        }
    }
}
