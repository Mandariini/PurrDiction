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
            public int dataOrigin, dataLength;
            public bool hasAddressedRecord;
            public PredictedComponentID recordId;
        }

        private readonly Entry[] _entries = new Entry[4];
        private ulong _tick = ulong.MaxValue;
        private int _count, _replacement;
        private int _lastEntry = -1;

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
            _lastEntry = -1;
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
                _lastEntry = i;
                if (entry.dataOrigin == 0)
                    destination.WriteBitsWithoutConsumingIt(entry.bits, entry.dataLength);
                else
                    destination.WriteBitDataWithoutConsumingIt(new BitData(entry.bits, entry.dataOrigin, entry.dataLength));
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
            entry.dataOrigin = 0;
            entry.dataLength = source.positionInBits - origin;
            entry.hasAddressedRecord = false;
            entry.baselineTick = baselineTick;
            entry.origin = origin;
            entry.baseline = baseline;
            entry.current = current;
            _lastEntry = index;
            if (_count < _entries.Length)
                _count++;
            else
                _replacement = (_replacement + 1) % _entries.Length;
        }

        public bool TryWriteAddressedRecord(ulong tick, ulong baselineTick, PredictedComponentID id,
            BitPacker destination, ref T baseline, ref T current, out int stateBits)
        {
            return TryWriteAddressedRecordAt(tick, FindAddressedRecord(tick, baselineTick, id),
                destination, ref baseline, ref current, out stateBits);
        }

        public int FindAddressedRecord(ulong tick, ulong baselineTick, PredictedComponentID id)
        {
            BeginTick(tick);
            for (int i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.hasAddressedRecord && entry.baselineTick == baselineTick && entry.recordId.Equals(id))
                    return i;
            }
            return -1;
        }

        public bool TryWriteAddressedRecordAt(ulong tick, int index, BitPacker destination,
            ref T baseline, ref T current, out int stateBits)
        {
            stateBits = 0;
            if (_tick != tick || index < 0 || index >= _count) return false;
            ref var entry = ref _entries[index];
            if (!entry.hasAddressedRecord || !Packer.AreEqualRef(ref entry.current, ref current) ||
                !Packer.AreEqualRef(ref entry.baseline, ref baseline)) return false;
            destination.WriteBitsWithoutConsumingIt(entry.bits, entry.bits.positionInBits);
            stateBits = entry.dataLength;
            return true;
        }

        public void StoreAddressedRecord(ulong tick, ulong baselineTick, PredictedComponentID id,
            BitPacker source, int recordOrigin)
        {
            if (_tick != tick || _lastEntry < 0) return;
            ref var entry = ref _entries[_lastEntry];
            if (entry.baselineTick != baselineTick) return;
            int recordLength = source.positionInBits - recordOrigin;
            entry.bits.ResetPositionAndMode(false);
            entry.bits.WriteBitDataWithoutConsumingIt(new BitData(source, recordOrigin, recordLength));
            // Identity state is the suffix; the same buffer still serves ordinary delta hits.
            entry.dataOrigin = recordLength - entry.dataLength;
            entry.recordId = id;
            entry.hasAddressedRecord = true;
        }

        // Pooled identities retain the buffers, just as they retain their history arrays.
        public void Reset()
        {
            _tick = ulong.MaxValue;
            _count = _replacement = 0;
            _lastEntry = -1;
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
