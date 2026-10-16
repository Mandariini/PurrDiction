using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class FrameStorageFastPathTests
    {
        private struct Value : IDisposable { public void Dispose() { } }

        [Test]
        public void SparseFindPreservesInsertionPointsAfterWrapAndRemoval()
        {
            var history = new History<Value>(20);
            // Repeated windows exercise both physical ring chunks and sparse tail gaps.
            for (ulong window = 0; window < 80; window++)
            {
                ulong tick = window * 10;
                history.Write(tick, default);
                history.Write(tick + 3, default);
                history.Write(tick + 8, default);
                if (window % 3 == 0) history.Remove(tick + 3);
                if (window % 5 == 0) history.Write(tick + 2, default);
                var ticks = new List<ulong>();
                for (int i = 0; i < history.Count; i++) ticks.Add(history.GetEntryTick(i));
                for (ulong query = ticks[0] == 0 ? 0 : ticks[0] - 1; query <= tick + 12; query++)
                {
                    int expected = ticks.BinarySearch(query);
                    bool found = expected >= 0;
                    if (!found) expected = ~expected;
                    Assert.That(history.Find(query, out int actual), Is.EqualTo(found), "tick " + query);
                    Assert.That(actual, Is.EqualTo(expected), "tick " + query);
                }
            }
            history.Clear();
            Assert.That(history.Find(0, out int emptyIndex), Is.False);
            Assert.That(emptyIndex, Is.Zero);
        }

        [Test]
        public void PrefixedRecordsMatchConcatenatedWireAtEveryDestinationAlignment()
        {
            NetworkManager.CallAllRegisters();
            var random = new Random(71);
            foreach (int bodyBits in new[] { 0, 1, 7, 63, 64, 65, 127, 256, 4097 })
            for (int alignment = 0; alignment < 8; alignment++)
            foreach (int prefixKind in new[] { 0, 1, 2 })
            {
                using var prefix = new BitPacker();
                using var body = new BitPacker();
                using var concatenated = new BitPacker();
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                if (prefixKind > 0)
                {
                    Packer<bool>.Write(prefix, prefixKind == 2);
                    if (prefixKind == 2) Packer<PackedUInt>.Write(prefix, (uint)65537);
                }
                for (int i = 0; i < bodyBits; i++) Packer<bool>.Write(body, random.Next(2) != 0);
                concatenated.WriteBitsWithoutConsumingIt(prefix, prefix.positionInBits);
                concatenated.WriteBitsWithoutConsumingIt(body, body.positionInBits);
                for (int i = 0; i < alignment; i++)
                {
                    Packer<bool>.Write(expected, true);
                    Packer<bool>.Write(actual, true);
                }
                var id = new PredictedComponentID(new PredictedObjectID(79), 2);
                AddressedPredictionRecords.WriteRecord(expected, id, prefixKind == 0, concatenated);
                AddressedPredictionRecords.WritePrefixedRecord(id, prefixKind == 0, prefix, body, actual);
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True,
                    $"body={bodyBits}, alignment={alignment}, prefix={prefixKind}");
                Assert.That(body.positionInBits, Is.EqualTo(bodyBits));
            }
        }
    }
}
