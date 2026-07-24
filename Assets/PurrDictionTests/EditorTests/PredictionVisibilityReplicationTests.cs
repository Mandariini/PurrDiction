using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionVisibilityReplicationTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType<PredictedObjectID>();
            Hasher.PrepareType<PredictedComponentID>();
        }

        [Test]
        public void AddressedRecordsRoundTripAcrossNonContiguousObjectsAndComponents()
        {
            var first = new PredictedComponentID(new PredictedObjectID(2), 0);
            var siblingComponent = new PredictedComponentID(new PredictedObjectID(2), 1);
            var distant = new PredictedComponentID(new PredictedObjectID(91), 0);

            using var frame = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();

            AddressedPredictionRecords.WriteSectionCount(3, frame);
            WriteIntRecord(frame, payload, first, 10);
            WriteIntRecord(frame, payload, siblingComponent, 20);
            WriteIntRecord(frame, payload, distant, 30);

            frame.ResetPositionAndMode(true);

            var decoded = new Dictionary<PredictedComponentID, int>();
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, isFull, record, _) =>
                {
                    Assert.That(isFull, Is.True);
                    decoded[id] = (int)record.ReadBits(32);
                });

            Assert.That(decoded[first], Is.EqualTo(10));
            Assert.That(decoded[siblingComponent], Is.EqualTo(20));
            Assert.That(decoded[distant], Is.EqualTo(30));
        }

        [Test]
        public void UnknownNonByteAlignedRecordCannotMisalignFollowingState()
        {
            var first = new PredictedComponentID(new PredictedObjectID(3), 0);
            var unknown = new PredictedComponentID(new PredictedObjectID(44), 7);
            var last = new PredictedComponentID(new PredictedObjectID(1000), 2);
            const uint sentinel = 0xC0FFEEu;

            using var frame = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();

            AddressedPredictionRecords.WriteSectionCount(3, frame);
            WriteIntRecord(frame, payload, first, 111);

            payload.ResetPositionAndMode(false);
            payload.WriteBits(0b101, 3);
            AddressedPredictionRecords.WriteRecord(frame, unknown, false, payload);

            WriteIntRecord(frame, payload, last, 999);
            Packer<uint>.Write(frame, sentinel);
            int writtenBits = frame.positionInBits;

            frame.ResetPositionAndMode(true);

            var decoded = new Dictionary<PredictedComponentID, int>();
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, _, record, _) =>
                {
                    if (id.Equals(unknown))
                        return;
                    decoded[id] = (int)record.ReadBits(32);
                });

            Assert.That(decoded[first], Is.EqualTo(111));
            Assert.That(decoded[last], Is.EqualTo(999));
            Assert.That(Packer<uint>.Read(frame), Is.EqualTo(sentinel));
            Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
        }

        [Test]
        public void LeaveAndReentryRequireANewAcknowledgedVisibilityGeneration()
        {
            var root = new PredictedObjectID(12);
            var desired = new HashSet<PredictedObjectID> { root };
            var timeline = new PlayerVisibilityTimeline();

            timeline.Record(10, desired);

            Assert.That(timeline.WasVisibleAt(root, 9), Is.False);
            Assert.That(timeline.WasVisibleAt(root, 10), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 9), Is.False);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 10), Is.True);

            desired.Clear();
            timeline.Record(20, desired);
            Assert.That(timeline.IsVisible(root), Is.False);

            desired.Add(root);
            timeline.Record(30, desired);

            Assert.That(timeline.WasVisibleAt(root, 15), Is.True);
            Assert.That(timeline.WasVisibleAt(root, 25), Is.False);
            Assert.That(timeline.WasVisibleAt(root, 30), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 15), Is.False,
                "an acknowledgement from the previous visibility generation cannot unlock deltas");
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 30), Is.True);
        }

        [Test]
        public void VisibilityTimelinesAreIsolatedPerReceiverAndSurvivePruning()
        {
            var rootA = new PredictedObjectID(20);
            var rootB = new PredictedObjectID(30);
            var first = new PlayerVisibilityTimeline();
            var second = new PlayerVisibilityTimeline();

            first.Record(5, new HashSet<PredictedObjectID> { rootA });
            second.Record(5, new HashSet<PredictedObjectID> { rootB });

            Assert.That(first.IsVisible(rootA), Is.True);
            Assert.That(first.IsVisible(rootB), Is.False);
            Assert.That(second.IsVisible(rootA), Is.False);
            Assert.That(second.IsVisible(rootB), Is.True);

            first.Record(8, new HashSet<PredictedObjectID> { rootB });
            first.PruneThrough(8);

            Assert.That(first.WasVisibleAt(rootA, 8), Is.False);
            Assert.That(first.WasVisibleAt(rootB, 8), Is.True);
            Assert.That(second.IsVisible(rootB), Is.True,
                "pruning one receiver must not mutate another receiver's visibility state");
        }

        [Test]
        public void PruningForgetsAcknowledgedInvisibleRoots()
        {
            var root = new PredictedObjectID(40);
            var timeline = new PlayerVisibilityTimeline();

            timeline.Record(5, new HashSet<PredictedObjectID> { root });
            timeline.Record(8, new HashSet<PredictedObjectID>());

            Assert.That(timeline.trackedRootCount, Is.EqualTo(1));

            timeline.PruneThrough(8);

            Assert.That(timeline.trackedRootCount, Is.Zero);
            Assert.That(timeline.WasVisibleAt(root, 8), Is.False);

            timeline.Record(10, new HashSet<PredictedObjectID> { root });
            Assert.That(timeline.WasVisibleAt(root, 10), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 10), Is.True);
        }

        [Test]
        public void HierarchyProjectionKeepsWholeRootsAndTheirPendingDeletes()
        {
            var visibleRoot = new PredictedObjectID(10);
            var hiddenRoot = new PredictedObjectID(20);
            var timeline = new PlayerVisibilityTimeline();
            timeline.Record(7, new HashSet<PredictedObjectID> { visibleRoot });

            var spawned = DisposableList<InstanceDetails>.Create(3);
            var deletes = DisposableList<PredictedObjectID>.Create(1);
            var source = default(PredictedHierarchyState);
            var projection = default(PredictedHierarchyState);
            GameObject hierarchyObject = null;

            try
            {
                spawned.Add(new InstanceDetails(
                    1, 0, visibleRoot, Vector3.zero, Quaternion.identity, null, null));
                spawned.Add(new InstanceDetails(
                    1, 1, new PredictedObjectID(11), Vector3.zero, Quaternion.identity, null, null));
                spawned.Add(new InstanceDetails(
                    2, 0, hiddenRoot, Vector3.zero, Quaternion.identity, null, null));
                deletes.Add(new PredictedObjectID(11));
                source = new PredictedHierarchyState(spawned, deletes, 123);

                hierarchyObject = new GameObject("Hierarchy visibility projection test");
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                projection = PredictedHierarchy.BuildVisibilityProjection(source, timeline, 7);

                Assert.That(projection.spawnedPrefabs.Count, Is.EqualTo(2));
                Assert.That(projection.spawnedPrefabs[0].instanceId, Is.EqualTo(visibleRoot));
                Assert.That(
                    projection.spawnedPrefabs[1].instanceId,
                    Is.EqualTo(new PredictedObjectID(11)));
                Assert.That(projection.toDelete.Count, Is.EqualTo(1));
                Assert.That(
                    projection.toDelete[0],
                    Is.EqualTo(new PredictedObjectID(11)));
                Assert.That(projection.nextInstanceId, Is.EqualTo(123));
            }
            finally
            {
                projection.Dispose();
                source.Dispose();
                if (hierarchyObject)
                    Object.DestroyImmediate(hierarchyObject);
            }
        }

        static void WriteIntRecord(
            BitPacker frame,
            BitPacker payload,
            PredictedComponentID id,
            int value)
        {
            payload.ResetPositionAndMode(false);
            payload.WriteBits((uint)value, 32);
            AddressedPredictionRecords.WriteRecord(frame, id, true, payload);
        }
    }
}
