using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    // Every pass walks all registered systems several times; an indexer that IL2CPP can inline
    // keeps those loops cheap. Indexing reads the live array, so loops observe insertions
    // made while they run exactly as they would with a List.
    internal sealed class PredictedSystemList : IEnumerable<PredictedIdentity>
    {
        private PredictedIdentity[] _items = new PredictedIdentity[16];
        private int _count;

        // Changes with every mutation, so per-tick scans can reuse rosters built from this list.
        public uint revision { get; private set; }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        public PredictedIdentity this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _items[index];
        }

        public void Add(PredictedIdentity item) => Insert(_count, item);

        public void Insert(int index, PredictedIdentity item)
        {
            if ((uint)index > (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_count == _items.Length)
                Array.Resize(ref _items, _items.Length * 2);
            if (index < _count)
                Array.Copy(_items, index, _items, index + 1, _count - index);
            _items[index] = item;
            _count++;
            revision++;
        }

        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            _count--;
            if (index < _count)
                Array.Copy(_items, index + 1, _items, index, _count - index);
            _items[_count] = null;
            revision++;
        }

        public bool Remove(PredictedIdentity item)
        {
            int index = IndexOf(item);
            if (index < 0)
                return false;
            RemoveAt(index);
            return true;
        }

        public int IndexOf(PredictedIdentity item) => Array.IndexOf(_items, item, 0, _count);

        public bool Contains(PredictedIdentity item) => IndexOf(item) >= 0;

        public void Reverse()
        {
            Array.Reverse(_items, 0, _count);
            revision++;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _count);
            _count = 0;
            revision++;
        }

        public IEnumerator<PredictedIdentity> GetEnumerator()
        {
            for (var i = 0; i < _count; i++)
                yield return _items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
