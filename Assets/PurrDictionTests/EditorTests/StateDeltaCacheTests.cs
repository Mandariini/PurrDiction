using NUnit.Framework;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class StateDeltaCacheTests
    {
        private struct State { public int value; }

        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void AddressedRecordReuseMovesAcrossOriginsAndRetainsTheStateSuffix(int origin)
        {
            using var cache = new StateDeltaCache<State>();
            using var body = BitPackerPool.Get();
            using var prefix = BitPackerPool.Get();
            using var record = BitPackerPool.Get();
            using var destination = BitPackerPool.Get();
            var baseline = new State { value = 3 };
            var current = new State { value = 97 };
            var id = new PredictedComponentID(new PredictedObjectID(400), 2);
            body.WriteBit(false); // Empty module set.
            body.WriteBit(true);
            body.WriteBits(94, 32);
            cache.Store(20, 10, 1, body, baseline, current);
            prefix.WriteBit(false); // Shared frame baseline.
            record.WriteBits(7, 3);
            int recordOrigin = record.positionInBits;
            AddressedPredictionRecords.WritePrefixedRecord(id, false, prefix, body, record);
            cache.StoreAddressedRecord(20, 10, id, record, recordOrigin);
            int length = record.positionInBits - recordOrigin;
            if (origin > 0) destination.WriteBits(0x7f, (byte)origin);
            Assert.That(cache.TryWriteAddressedRecord(20, 10, id, destination, ref baseline, ref current, out int stateBits), Is.True);
            Assert.That(stateBits, Is.EqualTo(33));
            Assert.That(new BitData(destination, origin, length).Equals(new BitData(record, recordOrigin, length)), Is.True);

            destination.ResetPositionAndMode(false);
            destination.WriteBit(false);
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.True);
            destination.ResetPositionAndMode(true);
            Assert.That(destination.ReadBit(), Is.False);
            Assert.That(destination.ReadBit(), Is.True);
            Assert.That(destination.ReadBits(32), Is.EqualTo(94UL));

            destination.ResetPositionAndMode(false);
            current.value++;
            Assert.That(cache.TryWriteAddressedRecord(20, 10, id, destination, ref baseline, ref current, out _), Is.False);
            current.value--;
            baseline.value++;
            Assert.That(cache.TryWriteAddressedRecord(20, 10, id, destination, ref baseline, ref current, out _), Is.False);
            baseline.value--;
            Assert.That(cache.TryWriteAddressedRecord(20, 11, id, destination, ref baseline, ref current, out _), Is.False);
            Assert.That(cache.TryWriteAddressedRecord(20, 10, default, destination, ref baseline, ref current, out _), Is.False);
            Assert.That(destination.positionInBits, Is.Zero);
            cache.Reset();
            Assert.That(cache.TryWriteAddressedRecord(20, 10, id, destination, ref baseline, ref current, out _), Is.False);
        }

        [Test]
        public void CustomHeaderWriterDisablesRecordReuseUntilRestored()
        {
            Assert.That(AddressedPredictionRecords.UsesStandardHeaders(), Is.True,
                Packer<PredictedComponentID>.WriteFunc.Method.DeclaringType.FullName);
            var original = Packer<PackedUInt>.WriteFunc;
            try
            {
                Packer<PackedUInt>.WriteFunc = (packer, value) => packer.WriteBits(value.value, 32);
                Assert.That(AddressedPredictionRecords.UsesStandardHeaders(), Is.False);
            }
            finally { Packer<PackedUInt>.WriteFunc = original; }
            Assert.That(AddressedPredictionRecords.UsesStandardHeaders(), Is.True);
            var originalId = Packer<PredictedComponentID>.WriteFunc;
            try
            {
                WriteFunc<PredictedComponentID> observer = (_, _) => { };
                Packer<PredictedComponentID>.WriteFunc = observer + originalId;
                Assert.That(AddressedPredictionRecords.UsesStandardHeaders(), Is.False,
                    "a multicast codec ending in the standard writer still has custom behavior");
            }
            finally { Packer<PredictedComponentID>.WriteFunc = originalId; }
            Assert.That(AddressedPredictionRecords.UsesStandardHeaders(), Is.True);
        }

        [Test]
        public void UnalignedSuffixIsCopiedExactlyAndDoesNotBorrowTheSourceBuffer()
        {
            using var cache = new StateDeltaCache<State>();
            using var source = BitPackerPool.Get();
            var baseline = new State { value = 3 };
            var current = new State { value = 97 };
            Packer<bool>.Write(source, true);
            Packer<int>.Write(source, current.value - baseline.value);
            cache.Store(20, 10, 1, source, baseline, current);
            source.ResetPositionAndMode(false);
            Packer<int>.Write(source, -123);
            using var destination = BitPackerPool.Get();
            Packer<bool>.Write(destination, false);
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.True);
            destination.ResetPositionAndMode(true);
            Assert.That(Packer<bool>.Read(destination), Is.False);
            Assert.That(Packer<int>.Read(destination), Is.EqualTo(94));
        }

        [Test]
        public void MutationPositionNewTickAndResetInvalidateReuse()
        {
            using var cache = new StateDeltaCache<State>();
            using var source = BitPackerPool.Get();
            var baseline = new State { value = 3 };
            var current = new State { value = 97 };
            Packer<int>.Write(source, 94);
            cache.Store(20, 10, 0, source, baseline, current);
            using var destination = BitPackerPool.Get();
            current.value++;
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            current.value--;
            baseline.value++;
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            baseline.value--;
            Packer<bool>.Write(destination, true);
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            destination.ResetPositionAndMode(false);
            Assert.That(cache.TryWrite(21, 10, destination, ref baseline, ref current), Is.False);
            cache.Store(21, 10, 0, source, baseline, current);
            cache.Reset();
            Assert.That(cache.TryWrite(21, 10, destination, ref baseline, ref current), Is.False);
        }

        [Test]
        public void MultipleBaselinesRemainIndependentAndCapacityIsBounded()
        {
            using var cache = new StateDeltaCache<State>();
            using var bits = BitPackerPool.Get();
            var current = new State { value = 100 };
            for (int i = 0; i < 5; i++)
            {
                bits.ResetPositionAndMode(false);
                Packer<int>.Write(bits, 100 - i);
                cache.Store(20, (ulong)(10 + i), 0, bits, new State { value = i }, current);
            }
            var evicted = new State { value = 0 };
            bits.ResetPositionAndMode(false);
            Assert.That(cache.TryWrite(20, 10, bits, ref evicted, ref current), Is.False);
            for (int i = 1; i < 5; i++)
            {
                bits.ResetPositionAndMode(false);
                var baseline = new State { value = i };
                Assert.That(cache.TryWrite(20, (ulong)(10 + i), bits, ref baseline, ref current), Is.True);
                bits.ResetPositionAndMode(true);
                Assert.That(Packer<int>.Read(bits), Is.EqualTo(100 - i));
            }
        }
    }
}
