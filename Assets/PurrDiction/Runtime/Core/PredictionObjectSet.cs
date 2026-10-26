using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    // Baseline membership only needs add, contains and clear. Keep IDs in one packed
    // array so hot lookups do not dispatch through an equality comparer.
    internal sealed class PredictionObjectSet : IReadOnlyCollection<PredictedObjectID>
    {
        private uint[] _values = Array.Empty<uint>();
        private int _shift = 32;
        private bool _hasZero;

        public int Count { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FindSlot(uint value)
        {
            int index = (int)(unchecked(value * 0x9E3779B9U) >> _shift);
            while (_values[index] != 0 && _values[index] != value)
                index = (index + 1) & (_values.Length - 1);
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(PredictedObjectID id)
        {
            uint value = id.instanceId.value;
            if (value == 0)
                return _hasZero;
            return _values.Length != 0 && _values[FindSlot(value)] == value;
        }

        public bool Add(PredictedObjectID id)
        {
            uint value = id.instanceId.value;
            if (value == 0)
            {
                if (_hasZero) return false;
                _hasZero = true;
                Count++;
                return true;
            }

            if (_values.Length == 0)
                Grow();
            int index = FindSlot(value);
            if (_values[index] == value)
                return false;
            if (Count >= _values.Length / 2)
            {
                Grow();
                index = FindSlot(value);
            }
            _values[index] = value;
            Count++;
            return true;
        }

        private void Grow()
        {
            var previous = _values;
            _values = new uint[previous.Length == 0 ? 16 : checked(previous.Length * 2)];
            _shift = previous.Length == 0 ? 28 : _shift - 1;
            for (int i = 0; i < previous.Length; i++)
            {
                uint value = previous[i];
                if (value != 0)
                    _values[FindSlot(value)] = value;
            }
        }

        public void Clear()
        {
            if (Count == 0) return;
            Array.Clear(_values, 0, _values.Length);
            Count = 0;
            _hasZero = false;
        }

        public IEnumerator<PredictedObjectID> GetEnumerator()
        {
            if (_hasZero) yield return new PredictedObjectID(0);
            for (int i = 0; i < _values.Length; i++)
                if (_values[i] != 0) yield return new PredictedObjectID(_values[i]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
