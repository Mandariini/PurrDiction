using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionVisibilityEdgeCaseTests
    {
        [Test]
        public void PendingDeletesSurviveSendAndSyntheticBaselineAcks()
        {
            var firstObject = new PredictedObjectID(10);
            var secondObject = new PredictedObjectID(20);
            var pending = new PlayerPendingVisibilityDeletes();
            var firstRecords = new List<InstanceDetails>
            {
                new InstanceDetails(1, firstObject, default, default, null)
            };
            var secondRecords = new List<InstanceDetails>
            {
                new InstanceDetails(2, secondObject, default, default, null)
            };

            var firstCurrent = EmptyHierarchyState();
            var firstBaseline = EmptyHierarchyState();
            var secondCurrent = EmptyHierarchyState();

            try
            {
                pending.Capture(firstObject, firstObject, firstRecords, 30);
                pending.Capture(firstObject, firstObject, firstRecords, 30);
                pending.PrepareCurrent(30, ref firstCurrent);

                Assert.That(firstCurrent.spawnedPrefabs.Count, Is.EqualTo(1));
                Assert.That(firstCurrent.toDelete[0], Is.EqualTo(firstObject));

                pending.Capture(secondObject, secondObject, secondRecords, 30);
                pending.MarkSent(30);
                pending.Acknowledge(30);

                Assert.That(pending.Count, Is.EqualTo(2),
                    "the sent tombstone remains the ACK baseline and the unprepared one remains unsent");

                pending.PrepareBaseline(30, ref firstBaseline);
                Assert.That(firstBaseline.spawnedPrefabs.Count, Is.EqualTo(1));
                Assert.That(firstBaseline.toDelete[0], Is.EqualTo(firstObject));

                Assert.That(pending.RequiresFullFrame(31), Is.True);
                pending.PrepareCurrent(31, ref secondCurrent);
                Assert.That(secondCurrent.spawnedPrefabs.Count, Is.EqualTo(1));
                Assert.That(secondCurrent.toDelete[0], Is.EqualTo(secondObject));

                pending.MarkSent(31);
                pending.Acknowledge(31);
                Assert.That(pending.Count, Is.EqualTo(1));
                Assert.That(pending.ContainsRoot(secondObject), Is.True);

                pending.Acknowledge(32);
                Assert.That(pending.Count, Is.Zero);
            }
            finally
            {
                secondCurrent.Dispose();
                firstBaseline.Dispose();
                firstCurrent.Dispose();
            }
        }

        static PredictedHierarchyState EmptyHierarchyState()
        {
            return new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(0),
                DisposableList<PredictedObjectID>.Create(0),
                21);
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void PhysicsProjectionRequiresBothEventEndpointsToBeVisible()
        {
            var visibleA = new PredictedObjectID(10);
            var visibleB = new PredictedObjectID(11);
            var hidden = new PredictedObjectID(20);
            var source = new PredictedPhysicsData
            {
                events = DisposableList<PhysicsEvent>.Create(3)
            };
            PredictedPhysicsData projection = default;

            try
            {
                source.events.Add(CreatePhysicsEvent(visibleA, visibleB));
                source.events.Add(CreatePhysicsEvent(visibleA, hidden));
                source.events.Add(CreatePhysicsEvent(hidden, visibleA));

                projection = PredictionPhysicsVisibility.Project(
                    source,
                    new HashSet<PredictedObjectID> { visibleA, visibleB });

                Assert.That(projection.events.Count, Is.EqualTo(1));
                Assert.That(projection.events[0].me.objectId, Is.EqualTo(visibleA));
                Assert.That(projection.events[0].other.objectId, Is.EqualTo(visibleB));
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
        public void Physics2DProjectionRequiresBothEventEndpointsToBeVisible()
        {
            var visibleA = new PredictedObjectID(30);
            var visibleB = new PredictedObjectID(31);
            var hidden = new PredictedObjectID(40);
            var source = new PredictedPhysics2DData
            {
                events = DisposableList<Physics2DEvent>.Create(3)
            };
            PredictedPhysics2DData projection = default;

            try
            {
                source.events.Add(CreatePhysics2DEvent(visibleA, visibleB));
                source.events.Add(CreatePhysics2DEvent(visibleA, hidden));
                source.events.Add(CreatePhysics2DEvent(hidden, visibleA));

                projection = PredictionPhysicsVisibility.Project(
                    source,
                    new HashSet<PredictedObjectID> { visibleA, visibleB });

                Assert.That(projection.events.Count, Is.EqualTo(1));
                Assert.That(projection.events[0].me.objectId, Is.EqualTo(visibleA));
                Assert.That(projection.events[0].other.objectId, Is.EqualTo(visibleB));
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
