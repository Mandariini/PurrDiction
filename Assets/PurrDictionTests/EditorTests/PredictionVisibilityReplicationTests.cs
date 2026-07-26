using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet;
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
            Hasher.PrepareType<PredictedIdentityState>();
        }

        [Test]
        public void DirectVisibilityMutationsCommitOnlyAtFrameBoundaries()
        {
            var managerObject = new GameObject("Event visibility manager test");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var player = new PlayerID(new PackedULong(7), false);
                var root = new PredictedObjectID(42);

                Assert.That(manager.HideFrom(player, root), Is.True);
                Assert.That(manager.HideFrom(player, root), Is.False);

                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.IsVisible(root), Is.False);
                Assert.That(timeline.latestTransitionTick, Is.EqualTo(10));

                Assert.That(manager.ShowTo(player, root), Is.True);
                Assert.That(timeline.IsVisible(root), Is.False,
                    "policy changes must wait for the next frame boundary");

                manager.PreparePlayerVisibility(player, 11, 10);
                Assert.That(timeline.IsVisible(root), Is.True);
                Assert.That(timeline.HasContinuousVisibilityFrom(root, 10), Is.False);
                Assert.That(timeline.HasContinuousVisibilityFrom(root, 11), Is.True);
                Assert.That(manager.ResetVisibility(player, root), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void OverlappingVisibilityAcquisitionsReleaseOnlyAfterTheFinalHandle()
        {
            var managerObject = new GameObject("Visibility acquisition test");
            IDisposable first = null;
            IDisposable second = null;
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var player = new PlayerID(new PackedULong(8), false);
                var root = new PredictedObjectID(43);

                manager.HideFrom(player, root);
                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.IsVisible(root), Is.False);

                first = manager.AcquireVisibility(player, root);
                second = manager.AcquireVisibility(player, root);
                manager.PreparePlayerVisibility(player, 11, 10);
                Assert.That(timeline.IsVisible(root), Is.True);

                first.Dispose();
                first.Dispose();
                manager.PreparePlayerVisibility(player, 12, 11);
                Assert.That(timeline.IsVisible(root), Is.True);

                second.Dispose();
                manager.PreparePlayerVisibility(player, 13, 12);
                Assert.That(timeline.IsVisible(root), Is.False);
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void StaleAcquisitionCannotAffectAReusedPlayerId()
        {
            var managerObject = new GameObject("Stale visibility acquisition test");
            IDisposable stale = null;
            IDisposable current = null;
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var player = new PlayerID(new PackedULong(9), false);
                var root = new PredictedObjectID(44);

                manager.HideFrom(player, root);
                stale = manager.AcquireVisibility(player, root);
                manager.PreparePlayerVisibility(player, 10, 0);
                manager.RemovePlayerVisibility(player);

                manager.HideFrom(player, root);
                current = manager.AcquireVisibility(player, root);
                var timeline = manager.PreparePlayerVisibility(player, 20, 0);
                Assert.That(timeline.IsVisible(root), Is.True);

                stale.Dispose();
                manager.PreparePlayerVisibility(player, 21, 20);
                Assert.That(timeline.IsVisible(root), Is.True,
                    "a handle from the previous observer generation must be inert");

                current.Dispose();
                manager.PreparePlayerVisibility(player, 22, 21);
                Assert.That(timeline.IsVisible(root), Is.False);
            }
            finally
            {
                current?.Dispose();
                stale?.Dispose();
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void DefaultVisibleRootIsNotTombstoneEligibleUntilProjected()
        {
            var managerObject = new GameObject("Unsent visibility root test");
            var hierarchyObject = new GameObject("Unsent visibility root hierarchy test");
            var state = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(10), false);
                var root = new PredictedObjectID(45);
                var record = new InstanceDetails(
                    1, 0, root, Vector3.zero, Quaternion.identity, null, null);
                state = HierarchyState(record);
                AttachHierarchy(manager, hierarchy, record);

                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                UpdateVisibilityFrame(manager, player, preparedTick: 0, sentTick: 0);

                Assert.That(timeline.IsVisible(root), Is.True);
                Assert.That(
                    manager.HasSentVisibilityRoot(player, timeline, root, 0),
                    Is.False,
                    "default-visible policy alone must not imply that root state was sent");

                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 10, state);
                Assert.That(
                    manager.HasSentVisibilityRoot(player, timeline, root, 0),
                    Is.True,
                    "a sent frame containing the root makes it tombstone eligible");
            }
            finally
            {
                state.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SentRootRetiresAfterHideAckAndReturnsOnlyAfterProjection()
        {
            var managerObject = new GameObject("Sent visibility root lifecycle test");
            var hierarchyObject = new GameObject("Sent visibility root hierarchy test");
            var state = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(11), false);
                var root = new PredictedObjectID(46);
                var record = new InstanceDetails(
                    1, 0, root, Vector3.zero, Quaternion.identity, null, null);
                state = HierarchyState(record);
                AttachHierarchy(manager, hierarchy, record);

                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, state);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 0), Is.True);

                manager.HideFrom(player, root);
                manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 9), Is.True,
                    "the previous visible frame remains eligible until the hide frame is sent");

                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 10, state);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 10), Is.False);

                manager.ShowTo(player, root);
                manager.PreparePlayerVisibility(player, 11, 10);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 10), Is.False,
                    "a policy show does not imply that a frame containing the root was prepared");

                RecordPreparedVisibilityFrame(manager, hierarchy, player, 11, state);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 10), Is.True,
                    "a prepared visible generation must be eligible before simulation can delete it");

                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 11, state);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 11), Is.True);
            }
            finally
            {
                state.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ShowingAbsentRootBeforeHideAckDoesNotCancelSentRootRetirement()
        {
            var managerObject = new GameObject("Absent visibility root ACK test");
            var hierarchyObject = new GameObject("Absent visibility root hierarchy test");
            var projection = default(PredictedHierarchyState);
            var empty = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(17), false);
                var root = new PredictedObjectID(47);
                var record = new InstanceDetails(
                    1, 0, root, Vector3.zero, Quaternion.identity, null, null);
                projection = HierarchyState(record);
                empty = EmptyHierarchyState();
                AttachHierarchy(manager, hierarchy);

                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, projection);

                manager.HideFrom(player, root);
                manager.PreparePlayerVisibility(player, 10, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 10, empty);
                manager.ShowTo(player, root);
                manager.PreparePlayerVisibility(player, 11, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 11, empty);

                Assert.That(timeline.IsVisible(root), Is.True);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 10), Is.False,
                    "policy restoration without a projected root must not preserve the old generation");

                AttachHierarchy(manager, hierarchy, record);
                RecordPreparedVisibilityFrame(manager, hierarchy, player, 12, projection);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 10), Is.True);
            }
            finally
            {
                empty.Dispose();
                projection.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void HidingAncestorCascadesThroughMultipleAttachmentLevelsAndAcquiringLeafPromotesAncestors()
        {
            var managerObject = new GameObject("Visibility dependency manager test");
            var hierarchyObject = new GameObject("Visibility dependency hierarchy test");
            IDisposable acquiredLeaf = null;
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var ancestor = new PredictedObjectID(50);
                var middle = new PredictedObjectID(60);
                var leaf = new PredictedObjectID(70);
                var sibling = new PredictedObjectID(71);
                var player = new PlayerID(new PackedULong(12), false);

                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        1, 0, ancestor, Vector3.zero, Quaternion.identity, null, null),
                    new InstanceDetails(
                        2, 0, middle, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(ancestor, 0)),
                    new InstanceDetails(
                        3, 0, leaf, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(middle, 0)),
                    new InstanceDetails(
                        4, 0, sibling, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(ancestor, 0)));

                manager.HideFrom(player, ancestor);
                var timeline = manager.PreparePlayerVisibility(player, 10, 0);

                Assert.That(timeline.IsVisible(ancestor), Is.False);
                Assert.That(timeline.IsVisible(middle), Is.False);
                Assert.That(timeline.IsVisible(leaf), Is.False,
                    "hiding an ancestor must transitively hide every attached descendant root");
                Assert.That(timeline.IsVisible(sibling), Is.False);

                acquiredLeaf = manager.AcquireVisibility(player, leaf);
                manager.PreparePlayerVisibility(player, 11, 10);

                Assert.That(timeline.IsVisible(ancestor), Is.True);
                Assert.That(timeline.IsVisible(middle), Is.True);
                Assert.That(timeline.IsVisible(leaf), Is.True,
                    "a forced-visible descendant needs its complete ancestor chain");
                Assert.That(timeline.IsVisible(sibling), Is.False,
                    "acquiring a leaf must not reveal hidden sibling branches");

                acquiredLeaf.Dispose();
                acquiredLeaf = null;
                manager.PreparePlayerVisibility(player, 12, 11);

                Assert.That(timeline.IsVisible(ancestor), Is.False);
                Assert.That(timeline.IsVisible(middle), Is.False);
                Assert.That(timeline.IsVisible(leaf), Is.False);
                Assert.That(timeline.IsVisible(sibling), Is.False);
            }
            finally
            {
                acquiredLeaf?.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void LosingLeafOwnershipReappliesHiddenAncestorCascade()
        {
            var managerObject = new GameObject("Ownership visibility manager test");
            var hierarchyObject = new GameObject("Ownership visibility hierarchy test");
            var identityObject = new GameObject("Owned leaf visibility identity test");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var identity = identityObject.AddComponent<VisibilityOwnerProbe>();
                var ancestor = new PredictedObjectID(72);
                var leaf = new PredictedObjectID(73);
                var sibling = new PredictedObjectID(74);
                var player = new PlayerID(new PackedULong(18), false);

                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        5, 0, ancestor, Vector3.zero, Quaternion.identity, null, null),
                    new InstanceDetails(
                        6, 0, leaf, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(ancestor, 0)),
                    new InstanceDetails(
                        7, 0, sibling, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(ancestor, 0)));
                RegisterVisibilitySystem(
                    manager,
                    identity,
                    new PredictedComponentID(leaf, 0),
                    player);

                manager.HideFrom(player, ancestor);
                var timeline = manager.PreparePlayerVisibility(player, 10, 0);

                Assert.That(timeline.IsVisible(ancestor), Is.True);
                Assert.That(timeline.IsVisible(leaf), Is.True,
                    "ownership forces the leaf and its required ancestor visible");
                Assert.That(timeline.IsVisible(sibling), Is.False,
                    "ownership must not reveal a hidden sibling branch");

                identity.owner = null;
                manager.PreparePlayerVisibility(player, 11, 10);

                Assert.That(timeline.IsVisible(ancestor), Is.False);
                Assert.That(timeline.IsVisible(leaf), Is.False,
                    "ownership loss must reapply the hidden ancestor cascade next frame");
                Assert.That(timeline.IsVisible(sibling), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RemovedDerivedDescendantsRetainHistoryUntilAckThenReturnToSparseDefault()
        {
            var managerObject = new GameObject("Derived visibility cleanup manager test");
            var hierarchyObject = new GameObject("Derived visibility cleanup hierarchy test");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(19), false);
                var ancestor = new PredictedObjectID(75);

                SetField(typeof(PredictionManager), manager,
                    "<cachedIsServer>k__BackingField", true);
                AttachHierarchy(manager, hierarchy, new InstanceDetails(
                    8, 0, ancestor, Vector3.zero, Quaternion.identity, null, null));

                manager.HideFrom(player, ancestor);
                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.currentExceptionCount, Is.EqualTo(1));
                Assert.That(timeline.trackedRootCount, Is.EqualTo(1));

                ulong tick = 10;
                for (var i = 0; i < 6; i++)
                {
                    var descendant = new PredictedObjectID((uint)(120 + i));
                    AttachHierarchy(manager, hierarchy, new InstanceDetails(
                        9 + i, 0, descendant, Vector3.zero, Quaternion.identity, null,
                        new PredictedComponentID(ancestor, 0)));
                    InvalidateVisibilityTopologyForTest(manager, hierarchy);

                    ulong hiddenTick = ++tick;
                    manager.PreparePlayerVisibility(player, hiddenTick, hiddenTick - 1);
                    Assert.That(timeline.IsVisible(descendant), Is.False);

                    RemoveHierarchyRecord(hierarchy, descendant);
                    InvalidateVisibilityTopologyForTest(manager, hierarchy);

                    ulong removedTick = ++tick;
                    manager.PreparePlayerVisibility(player, removedTick, hiddenTick);

                    Assert.That(timeline.IsVisible(descendant), Is.True,
                        "an absent descendant has no persistent hidden policy of its own");
                    Assert.That(timeline.WasVisibleAt(descendant, hiddenTick), Is.False,
                        "the pre-delete baseline must remain hidden until acknowledged");
                    Assert.That(timeline.WasVisibleAt(descendant, removedTick), Is.True);
                    Assert.That(timeline.currentExceptionCount, Is.EqualTo(1),
                        "only the explicitly hidden ancestor should remain an exception");
                    Assert.That(timeline.trackedRootCount, Is.EqualTo(2),
                        "restored history must remain until its transition is acknowledged");

                    manager.PreparePlayerVisibility(player, ++tick, removedTick);
                    Assert.That(timeline.currentExceptionCount, Is.EqualTo(1));
                    Assert.That(timeline.trackedRootCount, Is.EqualTo(1),
                        "acknowledged transient descendants must not accumulate");
                }
            }
            finally
            {
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ExplicitHiddenPolicySurvivesAbsenceAndIdReuse()
        {
            var managerObject = new GameObject("Explicit visibility policy manager test");
            var hierarchyObject = new GameObject("Explicit visibility policy hierarchy test");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(20), false);
                var root = new PredictedObjectID(130);

                SetField(typeof(PredictionManager), manager,
                    "<cachedIsServer>k__BackingField", true);
                AttachHierarchy(manager, hierarchy, new InstanceDetails(
                    20, 0, root, Vector3.zero, Quaternion.identity, null, null));

                manager.HideFrom(player, root);
                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.IsVisible(root), Is.False);

                RemoveHierarchyRecord(hierarchy, root);
                InvalidateVisibilityTopologyForTest(manager, hierarchy);
                manager.PreparePlayerVisibility(player, 11, 10);
                manager.PreparePlayerVisibility(player, 12, 11);

                Assert.That(timeline.IsVisible(root), Is.False);
                Assert.That(timeline.currentExceptionCount, Is.EqualTo(1));
                Assert.That(timeline.trackedRootCount, Is.EqualTo(1),
                    "explicit HideFrom remains a stable anchor while absent");

                AttachHierarchy(manager, hierarchy, new InstanceDetails(
                    21, 0, root, Vector3.zero, Quaternion.identity, null, null));
                InvalidateVisibilityTopologyForTest(manager, hierarchy);
                manager.PreparePlayerVisibility(player, 13, 12);
                Assert.That(timeline.IsVisible(root), Is.False,
                    "reusing an explicitly hidden root id must reapply the policy");

                manager.ShowTo(player, root);
                manager.PreparePlayerVisibility(player, 14, 13);
                manager.PreparePlayerVisibility(player, 15, 14);

                Assert.That(timeline.IsVisible(root), Is.True);
                Assert.That(timeline.currentExceptionCount, Is.Zero);
                Assert.That(timeline.trackedRootCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RootCreatedHiddenAndDeletedBeforeProjectionDoesNotQueueATombstone()
        {
            var managerObject = new GameObject("Unprojected visibility delete manager test");
            var hierarchyObject = new GameObject("Unprojected visibility delete hierarchy test");
            var empty = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(13), false);
                var root = new PredictedObjectID(80);

                AttachHierarchy(manager, hierarchy);
                empty = EmptyHierarchyState();
                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, empty);
                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        4, 0, root, Vector3.zero, Quaternion.identity, null, null));

                manager.HideFrom(player, root);
                manager.CapturePendingVisibilityDelete(root);

                Assert.That(GetPendingVisibilityDeletes(manager).Count, Is.Zero,
                    "a hidden root deleted before any projection must not manufacture a tombstone");
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 0), Is.False);

                manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.IsVisible(root), Is.False);
            }
            finally
            {
                empty.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RootFirstProjectedInPreparedFrameAndDeletedDuringSimulationQueuesATombstone()
        {
            var managerObject = new GameObject("Prepared visibility delete manager test");
            var hierarchyObject = new GameObject("Prepared visibility delete hierarchy test");
            var projection = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(19), false);
                var root = new PredictedObjectID(85);
                var record = new InstanceDetails(
                    5, 0, root, Vector3.zero, Quaternion.identity, null, null);
                AttachHierarchy(manager, hierarchy, record);
                projection = HierarchyState(record);

                var timeline = manager.PreparePlayerVisibility(player, 10, 0);
                RecordPreparedVisibilityFrame(manager, hierarchy, player, 10, projection);

                manager.CapturePendingVisibilityDelete(root);

                var pendingByPlayer = GetPendingVisibilityDeletes(manager);
                Assert.That(pendingByPlayer.TryGetValue(player, out var pending), Is.True);
                Assert.That(pending.Count, Is.EqualTo(1),
                    "the prepared hierarchy frame is sent after simulation, so its delete needs a tombstone");
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 0), Is.True);
            }
            finally
            {
                projection.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }
        [Test]
        public void SentRootRetiresAfterPermanentDeleteTombstoneIsAcknowledged()
        {
            var managerObject = new GameObject("Visibility delete retirement manager test");
            var hierarchyObject = new GameObject("Visibility delete retirement hierarchy test");
            var projection = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(14), false);
                var root = new PredictedObjectID(90);
                var record = new InstanceDetails(
                    5, 0, root, Vector3.zero, Quaternion.identity, null, null);

                AttachHierarchy(manager, hierarchy, record);
                projection = HierarchyState(record);

                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, projection);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 0), Is.True);

                manager.CapturePendingVisibilityDelete(root);
                var pendingByPlayer = GetPendingVisibilityDeletes(manager);
                Assert.That(pendingByPlayer.TryGetValue(player, out var pending), Is.True);
                Assert.That(pending.Count, Is.EqualTo(1));
                Assert.That(pending.GetObjectId(0), Is.EqualTo(root));

                RemoveHierarchyRecord(hierarchy, root);
                pending.MarkPrepared(10);
                pending.MarkSent(10);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 9), Is.True,
                    "the sent generation stays eligible while its tombstone is still riding");

                manager.PreparePlayerVisibility(player, 11, 11);

                Assert.That(pendingByPlayer.ContainsKey(player), Is.False);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 11), Is.False,
                    "the acknowledged delete must retire the old visible frame generation");
            }
            finally
            {
                projection.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SentRootRemainsTrackedWhileAnyPieceOfThatRootStillExists()
        {
            var managerObject = new GameObject("Partial visibility delete manager test");
            var hierarchyObject = new GameObject("Partial visibility delete hierarchy test");
            var projection = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(15), false);
                var root = new PredictedObjectID(100);
                var piece = new PredictedObjectID(101);
                var rootRecord = new InstanceDetails(
                    6, 0, root, Vector3.zero, Quaternion.identity, null, null);
                var pieceRecord = new InstanceDetails(
                    6, 1, piece, Vector3.zero, Quaternion.identity, null, null);

                AttachHierarchy(manager, hierarchy, rootRecord, pieceRecord);
                projection = HierarchyState(rootRecord, pieceRecord);

                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, projection);
                manager.CapturePendingVisibilityDelete(root);

                var pendingByPlayer = GetPendingVisibilityDeletes(manager);
                Assert.That(pendingByPlayer.TryGetValue(player, out var pending), Is.True);
                pending.MarkPrepared(10);
                pending.MarkSent(10);

                RemoveHierarchyRecord(hierarchy, root);
                Assert.That(hierarchy.TryGetRootId(piece, out var survivingRoot), Is.True);
                Assert.That(survivingRoot, Is.EqualTo(root));

                manager.PreparePlayerVisibility(player, 11, 11);

                Assert.That(pendingByPlayer.ContainsKey(player), Is.False,
                    "the acknowledged tombstone itself must stop riding");
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 11), Is.True,
                    "retirement must consider every record sharing the root, not only the root record key");
            }
            finally
            {
                projection.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void HiddenReusedRootDoesNotKeepTheDeletedGenerationTracked()
        {
            var managerObject = new GameObject("Reused visibility root manager test");
            var hierarchyObject = new GameObject("Reused visibility root hierarchy test");
            var projection = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var player = new PlayerID(new PackedULong(16), false);
                var root = new PredictedObjectID(110);
                var oldRecord = new InstanceDetails(
                    7, 0, root, Vector3.zero, Quaternion.identity, null, null);

                AttachHierarchy(manager, hierarchy, oldRecord);
                projection = HierarchyState(oldRecord);

                var timeline = manager.PreparePlayerVisibility(player, 9, 0);
                RecordSentVisibilityFrame(manager, hierarchy, player, timeline, 9, projection);
                manager.HideFrom(player, root);
                manager.PreparePlayerVisibility(player, 10, 0);
                Assert.That(timeline.IsVisible(root), Is.False);

                manager.CapturePendingVisibilityDelete(root);
                var pendingByPlayer = GetPendingVisibilityDeletes(manager);
                Assert.That(pendingByPlayer.TryGetValue(player, out var pending), Is.True);
                pending.MarkPrepared(10);
                pending.MarkSent(10);

                RemoveHierarchyRecord(hierarchy, root);
                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        8, 0, root, Vector3.zero, Quaternion.identity, null, null));

                manager.PreparePlayerVisibility(player, 11, 11);

                Assert.That(pendingByPlayer.ContainsKey(player), Is.False);
                Assert.That(manager.HasSentVisibilityRoot(player, timeline, root, 11), Is.False,
                    "a hidden ID reuse must not revive the acknowledged visible generation");
            }
            finally
            {
                projection.Dispose();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void BecomeHiddenRootIsOmittedFromBothAddressedStatePasses()
        {
            var managerObject = new GameObject("Become hidden write manager test");
            var hierarchyObject = new GameObject("Become hidden write hierarchy test");
            var hiddenObject = new GameObject("Become hidden regular identity test");
            var visibleObject = new GameObject("Still visible regular identity test");
            var hiddenEventObject = new GameObject("Become hidden event identity test");
            var visibleEventObject = new GameObject("Still visible event identity test");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var hiddenRoot = new PredictedObjectID(150);
                var visibleRoot = new PredictedObjectID(151);
                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        9, 0, hiddenRoot, Vector3.zero, Quaternion.identity, null, null),
                    new InstanceDetails(
                        10, 0, visibleRoot, Vector3.zero, Quaternion.identity, null, null));

                var hiddenIdentity =
                    hiddenObject.AddComponent<VisibilityAddressedWriteProbe>();
                var visibleIdentity =
                    visibleObject.AddComponent<VisibilityAddressedWriteProbe>();
                var hiddenEventIdentity =
                    hiddenEventObject.AddComponent<VisibilityAddressedEventWriteProbe>();
                var visibleEventIdentity =
                    visibleEventObject.AddComponent<VisibilityAddressedEventWriteProbe>();
                RegisterAddressedSystem(
                    manager, hiddenIdentity, new PredictedComponentID(hiddenRoot, 0));
                RegisterAddressedSystem(
                    manager, visibleIdentity, new PredictedComponentID(visibleRoot, 0));
                RegisterAddressedSystem(
                    manager, hiddenEventIdentity, new PredictedComponentID(hiddenRoot, 1));
                RegisterAddressedSystem(
                    manager, visibleEventIdentity, new PredictedComponentID(visibleRoot, 1));

                var timeline = new PlayerVisibilityTimeline();
                timeline.SetVisible(11, hiddenRoot, false);

                var regularIds = WriteAddressedStateSectionIds(
                    manager, timeline, 12, eventHandlers: false);
                Assert.That(regularIds, Does.Contain(visibleIdentity.id),
                    "the still-visible root must keep replicating");
                Assert.That(regularIds, Has.No.Member(hiddenIdentity.id),
                    "a root hidden before the written tick must not be serialized");
                Assert.That(regularIds.Count, Is.EqualTo(1));

                var eventIds = WriteAddressedStateSectionIds(
                    manager, timeline, 12, eventHandlers: true);
                Assert.That(eventIds, Does.Contain(visibleEventIdentity.id),
                    "the still-visible event handler must keep replicating");
                Assert.That(eventIds, Has.No.Member(hiddenEventIdentity.id),
                    "the event handler pass must apply the same visibility filter");
                Assert.That(eventIds.Count, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(visibleEventObject);
                Object.DestroyImmediate(hiddenEventObject);
                Object.DestroyImmediate(visibleObject);
                Object.DestroyImmediate(hiddenObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RemoteVisibilityDeletesApplyOnlyToInstancesAbsentFromTheDecodedState()
        {
            var managerObject = new GameObject("Remote visibility delete manager test");
            var hierarchyObject = new GameObject("Remote visibility delete hierarchy test");
            var doomedObject = new GameObject("Remote visibility delete doomed instance");
            var survivorObject = new GameObject("Remote visibility delete surviving instance");
            var state = default(PredictedHierarchyState);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var doomed = new PredictedObjectID(160);
                var survivor = new PredictedObjectID(161);
                var doomedRecord = new InstanceDetails(
                    -1, 0, doomed, Vector3.zero, Quaternion.identity, null, null);
                var survivorRecord = new InstanceDetails(
                    -1, 0, survivor, Vector3.zero, Quaternion.identity, null, null);

                AttachHierarchy(manager, hierarchy, doomedRecord, survivorRecord);
                SetField(
                    typeof(PredictedIdentity),
                    hierarchy,
                    "<predictionManager>k__BackingField",
                    manager);
                RegisterHierarchyInstance(hierarchy, doomed, doomedObject);
                RegisterHierarchyInstance(hierarchy, survivor, survivorObject);

                state = HierarchyState(survivorRecord);
                RecordHierarchyVerifiedState(manager, hierarchy, 12, state);

                var incoming = GetField<List<PredictedObjectID>>(
                    typeof(PredictionManager),
                    manager,
                    "_incomingVisibilityDeletes");
                incoming.Add(doomed);
                incoming.Add(survivor);
                ApplyPendingRemoteVisibilityDeletes(manager, 12);

                Assert.That(incoming, Is.Empty);
                Assert.That(hierarchy.TryGetRootId(doomed, out _), Is.False,
                    "an id absent from the decoded state must be deleted locally");
                Assert.That(hierarchy.TryGetGameObject(doomed, out _), Is.False);
                Assert.That(doomedObject.activeSelf, Is.False);
                Assert.That(hierarchy.TryGetRootId(survivor, out _), Is.True,
                    "an id the decoded state still contains belongs to the organic state path");
                Assert.That(hierarchy.ContainsSpawnedRoot(survivor), Is.True);

                incoming.Add(doomed);
                Assert.DoesNotThrow(
                    () => ApplyPendingRemoteVisibilityDeletes(manager, 12),
                    "re-delivering an already-applied delete must be a silent no-op");
                Assert.That(incoming, Is.Empty);
                Assert.That(hierarchy.TryGetRootId(survivor, out _), Is.True);
            }
            finally
            {
                state.Dispose();
                Object.DestroyImmediate(survivorObject);
                Object.DestroyImmediate(doomedObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
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
        public void DefaultVisibleTimelineStoresOnlyEffectiveExceptions()
        {
            var root = new PredictedObjectID(12);
            var timeline = new PlayerVisibilityTimeline();

            Assert.That(timeline.IsVisible(root), Is.True);
            Assert.That(timeline.WasVisibleAt(root, 0), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 0), Is.True);
            Assert.That(timeline.currentExceptionCount, Is.Zero);
            Assert.That(timeline.trackedRootCount, Is.Zero);

            Assert.That(timeline.SetVisible(10, root, true), Is.False);
            Assert.That(timeline.revision, Is.Zero);
            Assert.That(timeline.currentExceptionCount, Is.Zero);
        }

        [Test]
        public void LeaveAndReentryRequireANewAcknowledgedVisibilityGeneration()
        {
            var root = new PredictedObjectID(12);
            var timeline = new PlayerVisibilityTimeline();

            timeline.SetVisible(20, root, false);
            Assert.That(timeline.WasVisibleAt(root, 19), Is.True);
            Assert.That(timeline.WasVisibleAt(root, 20), Is.False);
            Assert.That(timeline.IsVisible(root), Is.False);

            timeline.SetVisible(30, root, true);

            Assert.That(timeline.WasVisibleAt(root, 15), Is.True);
            Assert.That(timeline.WasVisibleAt(root, 25), Is.False);
            Assert.That(timeline.WasVisibleAt(root, 30), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 15), Is.False,
                "an acknowledgement from the previous visibility generation cannot unlock deltas");
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 30), Is.True);
        }

        [Test]
        public void SameTickChangesCollapseToTheirFinalState()
        {
            var root = new PredictedObjectID(13);
            var timeline = new PlayerVisibilityTimeline();

            Assert.That(timeline.SetVisible(5, root, false), Is.True);
            Assert.That(timeline.SetVisible(5, root, true), Is.False,
                "same-tick cancellation leaves no net transition");
            Assert.That(timeline.IsVisible(root), Is.True);
            Assert.That(timeline.trackedRootCount, Is.Zero);
            Assert.That(timeline.latestTransitionTick, Is.Zero);

            timeline.SetVisible(6, root, false);
            timeline.SetVisible(6, root, true);
            timeline.SetVisible(6, root, false);

            Assert.That(timeline.IsVisible(root), Is.False);
            Assert.That(timeline.WasVisibleAt(root, 5), Is.True);
            Assert.That(timeline.WasVisibleAt(root, 6), Is.False);
            Assert.That(timeline.trackedRootCount, Is.EqualTo(1));
        }

        [Test]
        public void DefaultHiddenTimelineStoresVisibleExceptions()
        {
            var root = new PredictedObjectID(14);
            var timeline = new PlayerVisibilityTimeline(defaultVisible: false);

            Assert.That(timeline.IsVisible(root), Is.False);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 0), Is.False);

            timeline.SetVisible(5, root, true);
            timeline.PruneThrough(5);

            Assert.That(timeline.IsVisible(root), Is.True);
            Assert.That(timeline.currentExceptionCount, Is.EqualTo(1));
            Assert.That(timeline.trackedRootCount, Is.EqualTo(1));

            timeline.SetVisible(8, root, false);
            timeline.PruneThrough(8);

            Assert.That(timeline.IsVisible(root), Is.False);
            Assert.That(timeline.currentExceptionCount, Is.Zero);
            Assert.That(timeline.trackedRootCount, Is.Zero);
        }

        [Test]
        public void VisibilityTimelinesAreIsolatedPerReceiverAndSurvivePruning()
        {
            var rootA = new PredictedObjectID(20);
            var rootB = new PredictedObjectID(30);
            var first = new PlayerVisibilityTimeline();
            var second = new PlayerVisibilityTimeline();

            first.SetVisible(5, rootB, false);
            second.SetVisible(5, rootA, false);

            Assert.That(first.IsVisible(rootA), Is.True);
            Assert.That(first.IsVisible(rootB), Is.False);
            Assert.That(second.IsVisible(rootA), Is.False);
            Assert.That(second.IsVisible(rootB), Is.True);

            first.SetVisible(8, rootA, false);
            first.SetVisible(8, rootB, true);
            first.PruneThrough(8);

            Assert.That(first.WasVisibleAt(rootA, 8), Is.False);
            Assert.That(first.WasVisibleAt(rootB, 8), Is.True);
            Assert.That(second.IsVisible(rootB), Is.True,
                "pruning one receiver must not mutate another receiver's visibility state");
        }

        [Test]
        public void PruningRetainsActiveExceptionsAndForgetsRestoredDefaults()
        {
            var root = new PredictedObjectID(40);
            var timeline = new PlayerVisibilityTimeline();

            timeline.SetVisible(5, root, false);
            timeline.PruneThrough(5);

            Assert.That(timeline.trackedRootCount, Is.EqualTo(1));
            Assert.That(timeline.pruneCandidateCount, Is.Zero);
            Assert.That(timeline.IsVisible(root), Is.False);
            Assert.That(timeline.WasVisibleAt(root, 8), Is.False);

            timeline.SetVisible(10, root, true);
            timeline.PruneThrough(10);

            Assert.That(timeline.trackedRootCount, Is.Zero);
            Assert.That(timeline.currentExceptionCount, Is.Zero);
            Assert.That(timeline.IsVisible(root), Is.True);
            Assert.That(timeline.HasContinuousVisibilityFrom(root, 10), Is.True);
        }

        [Test]
        public void HierarchyProjectionKeepsWholeRootsAndTheirPendingDeletes()
        {
            var visibleRoot = new PredictedObjectID(10);
            var hiddenRoot = new PredictedObjectID(20);
            var timeline = new PlayerVisibilityTimeline();
            timeline.SetVisible(7, hiddenRoot, false);

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

        const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        static void AttachHierarchy(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            params InstanceDetails[] records)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<hierarchy>k__BackingField",
                hierarchy);

            var spawned = GetField<List<InstanceDetails>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_spawnedPrefabs");
            var recordsById = GetField<Dictionary<PredictedObjectID, InstanceDetails>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_recordsById");

            for (var i = 0; i < records.Length; i++)
            {
                spawned.Add(records[i]);
                recordsById.Add(records[i].instanceId, records[i]);
            }
        }

        static void RecordPreparedVisibilityFrame(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            PlayerID player,
            ulong tick,
            in PredictedHierarchyState state)
        {
            RecordHierarchyVerifiedState(manager, hierarchy, tick, state);
            UpdateVisibilityFrame(
                manager,
                player,
                preparedTick: tick,
                sentTick: null);
        }

        static void RecordSentVisibilityFrame(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick,
            in PredictedHierarchyState state)
        {
            RecordHierarchyVerifiedState(manager, hierarchy, tick, state);
            UpdateVisibilityFrame(
                manager,
                player,
                preparedTick: 0,
                sentTick: tick);
            manager.HandleVisibilityFrameSent(player, timeline, tick);
        }

        static void RecordHierarchyVerifiedState(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            ulong tick,
            in PredictedHierarchyState state)
        {
            var history = manager.GetVerifiedHistory<
                FULL_STATE<PredictedHierarchyState>>(hierarchy.id, out _);
            SetField(
                typeof(PredictedIdentity<PredictedHierarchyState>),
                hierarchy,
                "_verifiedHistory",
                history);

            var snapshot = new FULL_STATE<PredictedHierarchyState>
            {
                state = state.Duplicate()
            };
            history.Write(tick, snapshot);
        }

        static void UpdateVisibilityFrame(
            PredictionManager manager,
            PlayerID player,
            ulong preparedTick,
            ulong? sentTick)
        {
            var frames = GetField<List<PlayerPacker>>(
                typeof(PredictionManager),
                manager,
                "_clientFrames");

            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (!frame.player.Equals(player))
                    continue;

                frame.preparedVisibilityTick = preparedTick;
                if (sentTick.HasValue)
                    frame.sentVisibilityTick = sentTick.Value;
                frames[i] = frame;
                return;
            }

            frames.Add(new PlayerPacker
            {
                player = player,
                preparedVisibilityTick = preparedTick,
                sentVisibilityTick = sentTick ?? 0
            });
        }
        static void InvalidateVisibilityTopologyForTest(
            PredictionManager manager,
            PredictedHierarchy hierarchy)
        {
            SetField(
                typeof(PredictedHierarchy),
                hierarchy,
                "_visibilityDependencyCacheDirty",
                true);
            manager.HandleVisibilityTopologyChanged();
        }
        static void RemoveHierarchyRecord(
            PredictedHierarchy hierarchy,
            PredictedObjectID objectId)
        {
            var spawned = GetField<List<InstanceDetails>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_spawnedPrefabs");
            var recordsById = GetField<Dictionary<PredictedObjectID, InstanceDetails>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_recordsById");

            for (var i = spawned.Count - 1; i >= 0; i--)
            {
                if (spawned[i].instanceId.Equals(objectId))
                    spawned.RemoveAt(i);
            }

            recordsById.Remove(objectId);
        }

        static void RegisterVisibilitySystem(
            PredictionManager manager,
            PredictedIdentity identity,
            PredictedComponentID id,
            PlayerID owner)
        {
            identity.id = id;
            identity.owner = owner;
            SetField(
                typeof(PredictedIdentity),
                identity,
                "<predictionManager>k__BackingField",
                manager);
            SetField(
                typeof(PredictionManager),
                manager,
                "<cachedIsServer>k__BackingField",
                true);

            var systems = GetField<List<PredictedIdentity>>(
                typeof(PredictionManager),
                manager,
                "_systems");
            systems.Add(identity);
            SetField(
                typeof(PredictionManager),
                manager,
                "_systemsCount",
                systems.Count);
        }
        static Dictionary<PlayerID, PlayerPendingVisibilityDeletes>
            GetPendingVisibilityDeletes(PredictionManager manager)
        {
            return GetField<Dictionary<PlayerID, PlayerPendingVisibilityDeletes>>(
                typeof(PredictionManager),
                manager,
                "_pendingVisibilityDeletes");
        }

        static void RegisterAddressedSystem(
            PredictionManager manager,
            PredictedIdentity identity,
            PredictedComponentID id)
        {
            identity.id = id;
            SetField(
                typeof(PredictedIdentity),
                identity,
                "<predictionManager>k__BackingField",
                manager);

            var systems = GetField<List<PredictedIdentity>>(
                typeof(PredictionManager),
                manager,
                "_systems");
            systems.Add(identity);
            SetField(
                typeof(PredictionManager),
                manager,
                "_systemsCount",
                systems.Count);
        }

        static HashSet<PredictedComponentID> WriteAddressedStateSectionIds(
            PredictionManager manager,
            PlayerVisibilityTimeline timeline,
            ulong tick,
            bool eventHandlers)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteAddressedStateSection",
                InstanceFields);
            Assert.That(method, Is.Not.Null);

            using var frame = BitPackerPool.Get();
            method.Invoke(
                manager,
                new object[]
                {
                    default(PlayerID),
                    timeline,
                    frame,
                    tick,
                    0UL,
                    true,
                    eventHandlers
                });

            frame.ResetPositionAndMode(true);
            var ids = new HashSet<PredictedComponentID>();
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, _, _, _) => ids.Add(id));
            return ids;
        }

        static void RegisterHierarchyInstance(
            PredictedHierarchy hierarchy,
            PredictedObjectID objectId,
            GameObject go)
        {
            var instanceMap = GetField<Dictionary<PredictedObjectID, GameObject>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_instanceMap");
            var goToId = GetField<Dictionary<GameObject, PredictedObjectID>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_goToId");
            instanceMap[objectId] = go;
            goToId[go] = objectId;
        }

        static void ApplyPendingRemoteVisibilityDeletes(
            PredictionManager manager,
            ulong stateTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ApplyPendingRemoteVisibilityDeletes",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(manager, new object[] { stateTick });
        }

        static PredictedHierarchyState HierarchyState(params InstanceDetails[] records)
        {
            var spawned = DisposableList<InstanceDetails>.Create(records.Length);
            for (var i = 0; i < records.Length; i++)
                spawned.Add(records[i]);

            return new PredictedHierarchyState(
                spawned,
                DisposableList<PredictedObjectID>.Create(0),
                100);
        }

        static PredictedHierarchyState EmptyHierarchyState()
        {
            return HierarchyState();
        }

        static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }

        static void SetField(
            Type declaringType,
            object target,
            string fieldName,
            object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }

    public sealed class VisibilityOwnerProbe : PredictedIdentity<EmptyState>
    {
    }

    public class VisibilityAddressedWriteProbe : StatelessPredictedIdentity
    {
    }

    public sealed class VisibilityAddressedEventWriteProbe : VisibilityAddressedWriteProbe
    {
        internal override bool isEventHandler => true;
    }
}
