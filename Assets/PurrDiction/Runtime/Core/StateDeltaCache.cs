using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    // Enabled by codegen only for states whose instance fields contain no references,
    // native pointers or handles. Copies here are non-owning ordinary values.
    internal sealed class StateDeltaCache<T> : IDisposable where T : struct
    {
        private struct Entry
        {
            public ulong baselineTick;
            public int origin;
            public T baseline, current;
            public BitPacker bits;
        }

        private readonly Entry[] _entries = new Entry[4];
        private ulong _tick = ulong.MaxValue;
        private int _count, _replacement;

        public StateDeltaCache()
        {
            int capacity = Math.Max(32, System.Runtime.CompilerServices.Unsafe.SizeOf<T>() * 2 + 16);
            for (int i = 0; i < _entries.Length; i++)
                _entries[i].bits = new BitPacker(capacity);
        }

        private void BeginTick(ulong tick)
        {
            if (_tick == tick)
                return;
            _tick = tick;
            _count = _replacement = 0;
        }

        public bool TryWrite(ulong tick, ulong baselineTick, BitPacker destination,
            ref T baseline, ref T current)
        {
            BeginTick(tick);
            for (int i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.baselineTick != baselineTick || entry.origin != destination.positionInBits ||
                    !Packer.AreEqualRef(ref entry.current, ref current) ||
                    !Packer.AreEqualRef(ref entry.baseline, ref baseline))
                    continue;
                destination.WriteBitsWithoutConsumingIt(entry.bits, entry.bits.positionInBits);
                return true;
            }
            return false;
        }

        public void Store(ulong tick, ulong baselineTick, int origin, BitPacker source,
            in T baseline, in T current)
        {
            BeginTick(tick);
            int index = _count < _entries.Length ? _count : _replacement;
            ref var entry = ref _entries[index];
            entry.bits.ResetPositionAndMode(false);
            entry.bits.WriteBitDataWithoutConsumingIt(new BitData(source, origin, source.positionInBits - origin));
            entry.baselineTick = baselineTick;
            entry.origin = origin;
            entry.baseline = baseline;
            entry.current = current;
            if (_count < _entries.Length)
                _count++;
            else
                _replacement = (_replacement + 1) % _entries.Length;
        }

        // Pooled identities retain the buffers, just as they retain their history arrays.
        public void Reset()
        {
            _tick = ulong.MaxValue;
            _count = _replacement = 0;
            for (int i = 0; i < _entries.Length; i++)
                _entries[i].baseline = _entries[i].current = default;
        }

        public void Dispose()
        {
            Reset();
            for (int i = 0; i < _entries.Length; i++)
            {
                _entries[i].bits?.Dispose();
                _entries[i].bits = null;
            }
        }
    }
}
