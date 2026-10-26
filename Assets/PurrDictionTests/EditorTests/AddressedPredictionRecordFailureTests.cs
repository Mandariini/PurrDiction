using System;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class AddressedPredictionRecordFailureTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType<PredictedObjectID>();
            Hasher.PrepareType<PredictedComponentID>();
        }

        [Test]
        public void StateDecodeFailureRejectsTheContainingFrame()
        {
            using var frame = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();
            var id = new PredictedComponentID(new PredictedObjectID(5), 2);

            payload.WriteBits(0b101, 3);
            AddressedPredictionRecords.WriteSectionCount(1, frame);
            AddressedPredictionRecords.WriteRecord(frame, id, false, payload);
            frame.ResetPositionAndMode(true);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AddressedPredictionRecords.ReadSection(
                    source: frame,
                    readRecord: (_, _, _, _) => throw new ApplicationException("bad state")));

            Assert.That(exception.InnerException, Is.TypeOf<ApplicationException>());
        }

        [Test]
        public void InPlaceSectionsReadAndRecoverLikeStagedSections()
        {
            var ids = new[]
            {
                new PredictedComponentID(new PredictedObjectID(5), 0),
                new PredictedComponentID(new PredictedObjectID(6), 1),
                new PredictedComponentID(new PredictedObjectID(7), 2),
                new PredictedComponentID(new PredictedObjectID(8), 0)
            };
            // Exact, skipped, short and throwing readers in turn, at every frame bit offset.
            for (int offset = 0; offset < 8; offset++)
            foreach (bool inPlace in new[] { false, true })
            {
                using var frame = BitPackerPool.Get();
                Prefix(offset, frame);
                AddressedPredictionRecords.WriteSectionCount(ids.Length, frame);
                for (int i = 0; i < ids.Length; i++)
                {
                    using var payload = BitPackerPool.Get();
                    payload.WriteBits(0x2D5UL + (ulong)i, 10);
                    payload.WriteBits((0x7A3C1E5UL * (ulong)(i + 1)) & ((1UL << 27) - 1), 27);
                    AddressedPredictionRecords.WriteRecord(frame, ids[i], i == 1, payload);
                }
                frame.WriteBits(0x4A7, 11);
                frame.ResetPositionAndMode(true);
                frame.SetBitPosition(offset);

                var seen = new System.Collections.Generic.List<string>();
                var failures = new System.Collections.Generic.List<string>();
                AddressedPredictionRecords.ReadRecord reader = (id, full, packer, bits) =>
                {
                    int record = Array.IndexOf(ids, id);
                    Assert.That(bits, Is.EqualTo(37));
                    Assert.That(full, Is.EqualTo(record == 1));
                    if (record == 1)
                        return;
                    ulong head = packer.ReadBits(10);
                    seen.Add($"{record}:{head:X}");
                    if (record == 3)
                        throw new ApplicationException("bad state");
                    if (record == 0)
                        Assert.That(packer.ReadBits(27), Is.EqualTo(0x7A3C1E5UL));
                };
                AddressedPredictionRecords.RecordFailure onFailure = (id, error, declared, consumed) =>
                    failures.Add($"{Array.IndexOf(ids, id)}:{declared}:{consumed}:{error.GetType().Name}");

                if (inPlace)
                    AddressedPredictionRecords.ReadSectionInPlace(reader, frame, onFailure);
                else
                    AddressedPredictionRecords.ReadSection(reader, frame, onFailure);

                Assert.That(seen, Is.EqualTo(new[] { "0:2D5", "2:2D7", "3:2D8" }), $"offset {offset}, in place {inPlace}");
                Assert.That(failures, Is.EqualTo(new[]
                {
                    "2:37:10:InvalidOperationException",
                    "3:37:10:ApplicationException"
                }), $"offset {offset}, in place {inPlace}");
                Assert.That(frame.ReadBits(11), Is.EqualTo(0x4A7UL), "the section ends where its records end");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8193)]
        public void DirectSectionsMatchStagedWireBitsAcrossCountWidthsAndOffsets(int offset)
        {
            var counts = new[] { (0, 0), (1, 1), (127, 128), (128, 127), (129, 130),
                (16383, 16384), (16384, 16383), (int.MaxValue, 0), (0, int.MaxValue) };
            foreach (var (reserved, actual) in counts)
            foreach (int bits in new[] { 0, 1, 7, 8, 9, 63, 64, 65, 511, 512, 513, 4095 })
            {
                using var expected = BitPackerPool.Get();
                using var direct = BitPackerPool.Get();
                using var records = BitPackerPool.Get();
                Dirty(expected); Dirty(direct); Dirty(records);
                records.SetBitPosition(bits);
                Prefix(offset, expected);
                Prefix(offset, direct);
                AddressedPredictionRecords.WriteSectionCount(actual, expected);
                expected.WriteBitsWithoutConsumingIt(records, bits);

                AddressedPredictionRecords.WriteSectionCount(reserved, direct);
                int recordsOrigin = direct.positionInBits;
                direct.WriteBitsWithoutConsumingIt(records, bits);
                AddressedPredictionRecords.CompleteSection(actual, offset, recordsOrigin, direct);
                expected.WriteBits(0x4A7, 11);
                direct.WriteBits(0x4A7, 11);
                Assert.That(direct.positionInBits, Is.EqualTo(expected.positionInBits));
                Assert.That(new BitData(direct, 0, direct.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True,
                    $"offset {offset}, count {reserved}->{actual}, payload {bits}");
                direct.ResetPositionAndMode(true);
                direct.SetBitPosition(offset);
                Assert.That(Packer<PackedUInt>.Read(direct).value, Is.EqualTo((uint)actual));
            }
        }

        [TestCase(3, 11)]
        [TestCase(11, 3)]
        [TestCase(3, 10)]
        [TestCase(10, 3)]
        public void NonByteHeaderWidthChangesPreserveOpaqueRecords(int reservedBits, int actualBits)
        {
            for (int offset = 0; offset < 8; offset++)
            foreach (int bits in new[] { 0, 1, 7, 63, 64, 65, 511 })
            {
                using var expected = BitPackerPool.Get();
                using var direct = BitPackerPool.Get();
                using var records = BitPackerPool.Get();
                using var header = BitPackerPool.Get();
                Dirty(expected); Dirty(direct); Dirty(records); Dirty(header);
                records.SetBitPosition(bits);
                header.SetBitPosition(offset);
                header.WriteBits(0x5A7, (byte)actualBits);
                expected.WriteBits(0x55, (byte)offset);
                expected.WriteBitDataWithoutConsumingIt(new BitData(header, offset, actualBits));
                expected.WriteBitsWithoutConsumingIt(records, bits);
                direct.WriteBits(0x55, (byte)offset);
                direct.WriteBits(0, (byte)reservedBits);
                int recordsOrigin = direct.positionInBits;
                direct.WriteBitsWithoutConsumingIt(records, bits);
                AddressedPredictionRecords.ReplaceSectionHeader(offset, recordsOrigin, header, direct);
                expected.WriteBits(0x4A7, 11);
                direct.WriteBits(0x4A7, 11);
                Assert.That(direct.positionInBits, Is.EqualTo(expected.positionInBits));
                Assert.That(new BitData(direct, 0, direct.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True,
                    $"offset {offset}, header {reservedBits}->{actualBits}, payload {bits}");
            }
        }

        private static void Dirty(BitPacker packer)
        {
            for (ulong i = 0; i < 100; i++)
                packer.WriteBits(0x1A57C0DE394B62F8UL ^ (i * 0x103050709UL), 64);
            packer.SetBitPosition(0);
        }

        private static void Prefix(int bits, BitPacker packer)
        {
            while (bits >= 64)
            {
                packer.WriteBits(0x5A93C7E2468B1DF0UL, 64);
                bits -= 64;
            }
            if (bits != 0) packer.WriteBits(0x55, (byte)bits);
        }

        [Test]
        public void AddressedHelpersDoNotReplaceTheGlobalIntegerPacker()
        {
            const int sentinel = 123456789;
            using var packer = BitPackerPool.Get();

            packer.AdvanceBits(5);
            Packer<int>.Write(packer, sentinel);

            packer.ResetPositionAndMode(true);
            packer.SetBitPosition(5);

            Assert.That(Packer<int>.Read(packer), Is.EqualTo(sentinel));
        }
    }
}
