using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;
using InputHistorySpan = PurrNet.Prediction.PredictionManager.InputHistorySpan;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class UploadedInputPoolingTests
    {
        [Test]
        public void UploadStorageDoesNotBorrowOversizedPacketScratch()
        {
            using var fixture = new Fixture();
            var scratch = BitPackerPool.Get();
            scratch.EnsureBitsExist(65536 * 8);
            scratch.Dispose();
            fixture.manager.RecordUploadedInputs(10, fixture.bits, fixture.spans);
            var entry = fixture.Entry(10);
            Assert.That(entry.bits.buffer.Length, Is.LessThan(64));
            Assert.That(entry.bits.buffer[0], Is.EqualTo(7));
            Assert.That(entry.bits, Is.Not.SameAs(scratch));
        }

        [Test]
        public void ExpiredUploadBufferIsReusedWithoutChangingLiveUploads()
        {
            using var fixture = new Fixture();
            fixture.manager.RecordUploadedInputs(1, fixture.bits, fixture.spans);
            var first = fixture.Entry(1).bits;
            typeof(PredictionManager).GetField("_verifiedServerTick", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(fixture.manager, 1000UL);
            fixture.manager.RecordUploadedInputs(1000, fixture.bits, fixture.spans);
            var live = fixture.Entry(1000).bits;
            fixture.bits.ResetPositionAndMode(false);
            fixture.bits.WriteBits(99, 8);
            fixture.manager.RecordUploadedInputs(1001, fixture.bits, fixture.spans);
            Assert.That(fixture.Entry(1001).bits, Is.SameAs(first));
            Assert.That(fixture.Entry(1001).bits.buffer[0], Is.EqualTo(99));
            Assert.That(live.buffer[0], Is.EqualTo(7));
        }

        [Test]
        public void UploadKeepsAnIndependentCopyAndTheFirstUploadWins()
        {
            using var fixture = new Fixture();
            fixture.manager.RecordUploadedInputs(10, fixture.bits, fixture.spans);
            var first = fixture.Entry(10);
            fixture.spans[0] = new InputHistorySpan { id = new PredictedComponentID(new PredictedObjectID(99), 0) };
            fixture.bits.ResetPositionAndMode(false);
            fixture.bits.WriteBits(9, 8);
            fixture.manager.RecordUploadedInputs(10, fixture.bits, fixture.spans);
            Assert.That(fixture.Entry(10).spans, Is.SameAs(first.spans));
            Assert.That(first.spans[0].id.objectId.instanceId.value, Is.EqualTo(2));
            Assert.That(first.bits.buffer[0], Is.EqualTo(7));
            Assert.That(first.spans, Is.Not.SameAs(fixture.spans));
        }

        [Test]
        public void ExpiredUploadRostersAreReusedAndCleanupReturnsEveryLiveRoster()
        {
            var fixture = new Fixture();
            List<InputHistorySpan> first, second;
            try
            {
                fixture.manager.RecordUploadedInputs(1, fixture.bits, fixture.spans);
                first = fixture.Entry(1).spans;
                typeof(PredictionManager).GetField("_verifiedServerTick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(fixture.manager, 1000UL);
                fixture.manager.RecordUploadedInputs(1000, fixture.bits, fixture.spans);
                second = fixture.Entry(1000).spans;
                Assert.That(fixture.entries.Contains(1UL), Is.False);
                fixture.manager.RecordUploadedInputs(1001, fixture.bits, fixture.spans);
                Assert.That(fixture.Entry(1001).spans, Is.SameAs(first));
                Assert.That(first, Is.Not.SameAs(second));
            }
            finally { fixture.Dispose(); }
            Assert.That(first, Is.Empty);
            Assert.That(second, Is.Empty);
            Assert.That(fixture.spans, Has.Count.EqualTo(1));
            var rentedA = ListPool<InputHistorySpan>.Instantiate(1);
            var rentedB = ListPool<InputHistorySpan>.Instantiate(1);
            try { Assert.That(rentedA, Is.Not.SameAs(rentedB), "an upload roster was returned twice"); }
            finally { ListPool<InputHistorySpan>.Destroy(rentedA); ListPool<InputHistorySpan>.Destroy(rentedB); }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject gameObject = new("Uploaded input pooling");
            public readonly PredictionManager manager;
            public readonly BitPacker bits = BitPackerPool.Get();
            public readonly List<InputHistorySpan> spans = new()
            {
                new InputHistorySpan { id = new PredictedComponentID(new PredictedObjectID(2), 0), bitLength = 8 }
            };
            public readonly IDictionary entries;

            public Fixture()
            {
                manager = gameObject.AddComponent<PredictionManager>();
                entries = (IDictionary)typeof(PredictionManager).GetField("_uploadedInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(manager);
                bits.WriteBits(7, 8);
            }

            public (BitPacker bits, List<InputHistorySpan> spans) Entry(ulong tick)
            {
                var entry = entries[tick];
                return ((BitPacker)entry.GetType().GetField("bits").GetValue(entry),
                    (List<InputHistorySpan>)entry.GetType().GetField("spans").GetValue(entry));
            }

            public void Dispose()
            {
                typeof(PredictionManager).GetMethod("CleanupAllSystems", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                Object.DestroyImmediate(gameObject);
                bits.Dispose();
            }
        }
    }
}
