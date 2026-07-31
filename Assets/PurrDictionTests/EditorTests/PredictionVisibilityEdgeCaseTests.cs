using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionVisibilityEdgeCaseTests
    {
        [Test]
        public void PendingDeletesRideEveryFrameUntilACarriedFrameIsAcknowledged()
        {
            var firstObject = new PredictedObjectID(10);
            var firstRoot = new PredictedObjectID(11);
            var secondObject = new PredictedObjectID(20);
            var secondRoot = new PredictedObjectID(21);
            var pending = new PlayerPendingVisibilityDeletes();
            var acknowledgedRoots = new List<PredictedObjectID>();

            pending.Capture(firstObject, firstRoot);
            pending.MarkPrepared(30);
            pending.Capture(secondObject, secondRoot);
            pending.MarkSent(30);

            Assert.That(pending.Count, Is.EqualTo(2));
            Assert.That(pending.GetObjectId(0), Is.EqualTo(firstObject));
            Assert.That(pending.GetObjectId(1), Is.EqualTo(secondObject));

            pending.Acknowledge(29, acknowledgedRoots);
            Assert.That(pending.Count, Is.EqualTo(2),
                "an acknowledgement older than the first carrying frame retires nothing");
            Assert.That(acknowledgedRoots, Is.Empty);

            pending.Acknowledge(30, acknowledgedRoots);
            Assert.That(pending.Count, Is.EqualTo(1),
                "acknowledging the carrying frame retires only the entries it carried");
            Assert.That(acknowledgedRoots, Is.EqualTo(new List<PredictedObjectID> { firstRoot }));
            Assert.That(pending.ContainsRoot(firstRoot), Is.False);
            Assert.That(pending.ContainsRoot(secondRoot), Is.True);
            Assert.That(pending.GetObjectId(0), Is.EqualTo(secondObject));

            pending.MarkPrepared(31);
            pending.MarkSent(31);
            pending.Acknowledge(31);
            Assert.That(pending.Count, Is.Zero);
        }

        [Test]
        public void PendingDeleteThatNeverRodeASentFrameSurvivesEveryAcknowledgement()
        {
            var objectId = new PredictedObjectID(10);
            var rootId = new PredictedObjectID(11);
            var pending = new PlayerPendingVisibilityDeletes();

            pending.Capture(objectId, rootId);
            pending.Acknowledge(ulong.MaxValue);
            Assert.That(pending.Count, Is.EqualTo(1),
                "an entry that never rode a sent frame has no acknowledgeable send");

            pending.MarkPrepared(40);
            pending.Acknowledge(ulong.MaxValue);
            Assert.That(pending.Count, Is.EqualTo(1),
                "preparing a frame is not sending it");
        }

        [Test]
        public void MarkSentIgnoresTicksThatDidNotCarryThePreparedEntry()
        {
            var objectId = new PredictedObjectID(10);
            var rootId = new PredictedObjectID(11);
            var pending = new PlayerPendingVisibilityDeletes();

            pending.Capture(objectId, rootId);
            pending.MarkPrepared(30);
            pending.MarkSent(31);
            pending.Acknowledge(31);
            Assert.That(pending.Count, Is.EqualTo(1),
                "a send that did not match the prepared tick must not arm the entry");

            pending.MarkSent(30);
            pending.Acknowledge(30);
            Assert.That(pending.Count, Is.Zero);
        }

        [Test]
        public void RidingEntryKeepsItsFirstSentTickAsTheAcknowledgementBaseline()
        {
            var objectId = new PredictedObjectID(10);
            var rootId = new PredictedObjectID(11);
            var pending = new PlayerPendingVisibilityDeletes();

            pending.Capture(objectId, rootId);
            pending.MarkPrepared(30);
            pending.MarkSent(30);

            pending.MarkPrepared(31);
            pending.MarkSent(31);
            Assert.That(pending.Count, Is.EqualTo(1),
                "the entry keeps riding subsequent frames until acknowledged");
            Assert.That(pending.GetObjectId(0), Is.EqualTo(objectId));

            pending.Acknowledge(30);
            Assert.That(pending.Count, Is.Zero,
                "the first frame that carried the entry remains the acknowledgement baseline");
        }

        [Test]
        public void CaptureDeduplicatesEntriesByObjectId()
        {
            var objectId = new PredictedObjectID(10);
            var firstRoot = new PredictedObjectID(11);
            var secondRoot = new PredictedObjectID(12);
            var otherObject = new PredictedObjectID(20);
            var pending = new PlayerPendingVisibilityDeletes();

            pending.Capture(objectId, firstRoot);
            pending.Capture(objectId, secondRoot);

            Assert.That(pending.Count, Is.EqualTo(1));
            Assert.That(pending.ContainsRoot(firstRoot), Is.True,
                "the first capture wins; a duplicate must not retarget the root");
            Assert.That(pending.ContainsRoot(secondRoot), Is.False);

            pending.Capture(otherObject, firstRoot);
            Assert.That(pending.Count, Is.EqualTo(2));
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void PhysicsProjectionFiltersOnlyHiddenEventEndpoints()
        {
            var visibleA = new PredictedObjectID(10);
            var visibleB = new PredictedObjectID(11);
            var hidden = new PredictedObjectID(20);
            // Not owned by the predicted hierarchy at all (scene collider, runtime-created
            // identity). It appears in no membership set and must stay visible.
            var sceneObject = new PredictedObjectID(30);
            var source = new PredictedPhysicsData
            {
                events = DisposableList<PhysicsEvent>.Create(4)
            };
            PredictedPhysicsData projection = default;

            try
            {
                source.events.Add(CreatePhysicsEvent(visibleA, visibleB));
                source.events.Add(CreatePhysicsEvent(visibleA, hidden));
                source.events.Add(CreatePhysicsEvent(hidden, visibleA));
                source.events.Add(CreatePhysicsEvent(visibleA, sceneObject));

                projection = PredictionPhysicsVisibility.Project(
                    source,
                    new HashSet<PredictedObjectID> { hidden });

                Assert.That(projection.events.Count, Is.EqualTo(2));
                Assert.That(projection.events[0].me.objectId, Is.EqualTo(visibleA));
                Assert.That(projection.events[0].other.objectId, Is.EqualTo(visibleB));
                Assert.That(projection.events[1].other.objectId, Is.EqualTo(sceneObject),
                    "an object the hierarchy does not track must not be filtered out");
            }
            finally
            {
                projection.Dispose();
                source.Dispose();
            }
        }

        static PhysicsEvent CreatePhysicsEvent(
            PredictedObjectID me,
            PredictedObjectID other)
        {
            return new PhysicsEvent
            {
                me = new PredictedComponentID(me, 0),
                other = new PredictedComponentID(other, 0),
                collision = new PhysicsCollision
                {
                    contacts = DisposableList<PhysicsContactPoint>.Create(0)
                }
            };
        }
#endif

#if UNITY_PHYSICS_2D
        [Test]
        public void Physics2DProjectionFiltersOnlyHiddenEventEndpoints()
        {
            var visibleA = new PredictedObjectID(30);
            var visibleB = new PredictedObjectID(31);
            var hidden = new PredictedObjectID(40);
            var sceneObject = new PredictedObjectID(50);
            var source = new PredictedPhysics2DData
            {
                events = DisposableList<Physics2DEvent>.Create(4)
            };
            PredictedPhysics2DData projection = default;

            try
            {
                source.events.Add(CreatePhysics2DEvent(visibleA, visibleB));
                source.events.Add(CreatePhysics2DEvent(visibleA, hidden));
                source.events.Add(CreatePhysics2DEvent(hidden, visibleA));
                source.events.Add(CreatePhysics2DEvent(visibleA, sceneObject));

                projection = PredictionPhysicsVisibility.Project(
                    source,
                    new HashSet<PredictedObjectID> { hidden });

                Assert.That(projection.events.Count, Is.EqualTo(2));
                Assert.That(projection.events[0].me.objectId, Is.EqualTo(visibleA));
                Assert.That(projection.events[0].other.objectId, Is.EqualTo(visibleB));
                Assert.That(projection.events[1].other.objectId, Is.EqualTo(sceneObject),
                    "an object the hierarchy does not track must not be filtered out");
            }
            finally
            {
                projection.Dispose();
                source.Dispose();
            }
        }

        static Physics2DEvent CreatePhysics2DEvent(
            PredictedObjectID me,
            PredictedObjectID other)
        {
            return new Physics2DEvent
            {
                me = new PredictedComponentID(me, 0),
                other = new PredictedComponentID(other, 0),
                contacts = DisposableList<Physics2DContactPoint>.Create(0)
            };
        }
#endif
    }
}
