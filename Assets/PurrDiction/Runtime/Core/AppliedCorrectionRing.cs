using System;

namespace PurrNet.Prediction
{
    internal sealed class AppliedCorrectionRing<T> where T : struct
    {
        private const int DefaultCapacity = 20 * 10;

        private readonly ulong[] _ticks;
        private readonly T[] _values;
        private readonly bool[] _valid;

        public AppliedCorrectionRing(int capacity = DefaultCapacity)
        {
            capacity = Math.Max(1, capacity);
            _ticks = new ulong[capacity];
            _values = new T[capacity];
            _valid = new bool[capacity];
        }

        public void Record(ulong tick, in T totals)
        {
            int index = (int)(tick % (ulong)_ticks.Length);
            _ticks[index] = tick;
            _values[index] = totals;
            _valid[index] = true;
        }

        /// <summary>
        /// Returns the totals snapshot taken at the start of <paramref name="tick"/>. When that slot
        /// was overwritten because the tick is too old, falls back to the OLDEST retained tick after it,
        /// yielding partial (never over-counted) compensation instead of none. False when nothing usable remains.
        /// </summary>
        public bool TryGetBaseline(ulong tick, out T totals)
        {
            int index = (int)(tick % (ulong)_ticks.Length);
            if (_valid[index] && _ticks[index] == tick)
            {
                totals = _values[index];
                return true;
            }

            ulong bestTick = ulong.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < _ticks.Length; i++)
            {
                if (!_valid[i])
                    continue;

                ulong entryTick = _ticks[i];
                if (entryTick > tick && entryTick < bestTick)
                {
                    bestTick = entryTick;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                totals = default;
                return false;
            }

            totals = _values[bestIndex];
            return true;
        }

        public void Clear()
        {
            Array.Clear(_ticks, 0, _ticks.Length);
            Array.Clear(_values, 0, _values.Length);
            Array.Clear(_valid, 0, _valid.Length);
        }
    }
}
