using System;
using Unity.IL2CPP.CompilerServices;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Maps a content hash to an entry index for one tick's inputs. It is cleared every tick for
    /// every input, so a stamp per slot replaces clearing and linear probing over a power-of-two
    /// table replaces a dictionary lookup.
    /// </summary>
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    internal sealed class ContentIndex
    {
        private ulong[] _keys = new ulong[64];
        private int[] _values = new int[64];
        private uint[] _stamps = new uint[64];
        private int _mask = 63;
        private uint _stamp = 1;
        private int _count;

        internal int Count => _count;

        internal void Clear()
        {
            _count = 0;
            if (++_stamp == 0)
            {
                Array.Clear(_stamps, 0, _stamps.Length);
                _stamp = 1;
            }
        }

        internal bool TryGetValue(ulong key, out int value)
        {
            int slot = Slot(key);
            while (_stamps[slot] == _stamp)
            {
                if (_keys[slot] == key)
                {
                    value = _values[slot];
                    return true;
                }
                slot = (slot + 1) & _mask;
            }
            value = 0;
            return false;
        }

        internal void Set(ulong key, int value)
        {
            if ((_count + 1) * 2 > _keys.Length)
                Grow();
            int slot = Slot(key);
            while (_stamps[slot] == _stamp)
            {
                if (_keys[slot] == key)
                {
                    _values[slot] = value;
                    return;
                }
                slot = (slot + 1) & _mask;
            }
            _stamps[slot] = _stamp;
            _keys[slot] = key;
            _values[slot] = value;
            _count++;
        }

        private int Slot(ulong key)
        {
            key ^= key >> 33;
            key *= 0xff51afd7ed558ccdUL;
            key ^= key >> 33;
            return (int)key & _mask;
        }

        private void Grow()
        {
            var keys = _keys;
            var values = _values;
            var stamps = _stamps;
            uint stamp = _stamp;
            int capacity = keys.Length * 2;
            _keys = new ulong[capacity];
            _values = new int[capacity];
            _stamps = new uint[capacity];
            _mask = capacity - 1;
            _stamp = 1;
            _count = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (stamps[i] == stamp)
                    Set(keys[i], values[i]);
            }
        }
    }
}
