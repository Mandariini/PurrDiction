using System;

namespace PurrNet.Prediction
{
    internal sealed class AppliedCorrectionRing<T> where T : struct
    {
        private const int Size = 128;

        private readonly ulong[] _ticks = new ulong[Size];
        private readonly T[] _values = new T[Size];
        private readonly bool[] _valid = new bool[Size];

        public void Record(ulong tick, in T totals)
        {
            int index = (int)(tick % Size);
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
            int index = (int)(tick % Size);
            if (_valid[index] && _ticks[index] == tick)
            {
                totals = _values[index];
                return true;
            }

            ulong bestTick = ulong.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < Size; i++)
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
            Array.Clear(_ticks, 0, Size);
            Array.Clear(_values, 0, Size);
            Array.Clear(_valid, 0, Size);
        }
    }
}
