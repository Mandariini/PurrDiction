using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class EmptyPhysicsSnapshotTests
    {
        [OneTimeSetUp]
        public void Register() => NetworkManager.CallAllRegisters();

        [Test]
        public void Empty3DSnapshotsHaveIndependentEmptyStorageAndUnchangedWire()
        {
            var held = Drain<PhysicsEvent>();
            var original = new PredictedPhysicsData { events = DisposableList<PhysicsEvent>.Create(16) };
            var copy = PurrCopy<PredictedPhysicsData>.Copy(original);
            var live = PurrCopy<PredictedPhysicsData>.Copy(copy);
            var obj = new GameObject("3D snapshot restore");
            try
            {
                Assert.That(copy.events.isDisposed, Is.False);
                Assert.That(copy.events.list.Capacity, Is.Zero);
                Assert.That(copy.events.list, Is.Not.SameAs(original.events.list));
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                Packer<PredictedPhysicsData>.Write(expected, original);
                Packer<PredictedPhysicsData>.Write(actual, copy);
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True);
                var system = obj.AddComponent<Predicted3DPhysics>();
                typeof(Predicted3DPhysics).GetMethod("SetUnityState", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(system, new object[] { live });
                Assert.That(live.events.list.Capacity, Is.GreaterThanOrEqualTo(16));
                Assert.That(copy.events.list.Capacity, Is.Zero, "restoring live capacity must not mutate a history batch");
                live.events.Add(new PhysicsEvent { isTrigger = true, type = PhysicsEventType.Enter });
                Assert.That(copy.events.Count, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(obj);
                live.Dispose(); copy.Dispose(); original.Dispose();
                foreach (var list in held) ListPool<PhysicsEvent>.Destroy(list);
            }
        }

        [Test]
        public void Empty2DSnapshotsHaveIndependentEmptyStorageAndPreserveLiveHeadroom()
        {
            var held = Drain<Physics2DEvent>();
            var original = new PredictedPhysics2DData { events = DisposableList<Physics2DEvent>.Create(16) };
            var copy = PurrCopy<PredictedPhysics2DData>.Copy(original);
            var live = PurrCopy<PredictedPhysics2DData>.Copy(copy);
            var obj = new GameObject("2D snapshot restore");
            try
            {
                Assert.That(copy.events.isDisposed, Is.False);
                Assert.That(copy.events.list.Capacity, Is.Zero);
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                Packer<PredictedPhysics2DData>.Write(expected, original);
                Packer<PredictedPhysics2DData>.Write(actual, copy);
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True);
                var system = obj.AddComponent<Predicted2DPhysics>();
                typeof(Predicted2DPhysics).GetMethod("SetUnityState", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(system, new object[] { live });
                Assert.That(live.events.list.Capacity, Is.GreaterThanOrEqualTo(16));
                Assert.That(copy.events.list.Capacity, Is.Zero);
                live.events.Add(new Physics2DEvent { isTrigger = true, type = PhysicsEventType.Enter });
                Assert.That(copy.events.Count, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(obj);
                live.Dispose(); copy.Dispose(); original.Dispose();
                foreach (var list in held) ListPool<Physics2DEvent>.Destroy(list);
            }
        }

        [Test]
        public void Nonempty3DSnapshotsKeepIndependentContactOwnership()
        {
            var original = new PredictedPhysicsData { events = DisposableList<PhysicsEvent>.Create(1) };
            var contacts = DisposableList<PhysicsContactPoint>.Create(1);
            contacts.Add(default);
            original.events.Add(new PhysicsEvent { collision = new PhysicsCollision { contacts = contacts } });
            var copy = PurrCopy<PredictedPhysicsData>.Copy(original);
            try
            {
                Assert.That(copy.events.list, Is.Not.SameAs(original.events.list));
                Assert.That(copy.events[0].collision.contacts.list, Is.Not.SameAs(contacts.list));
                original.Dispose();
                Assert.That(copy.events.Count, Is.EqualTo(1));
                Assert.That(copy.events[0].collision.contacts.Count, Is.EqualTo(1));
            }
            finally { copy.Dispose(); original.Dispose(); }
        }

        [Test]
        public void Nonempty2DSnapshotsKeepIndependentContactOwnership()
        {
            var original = new PredictedPhysics2DData { events = DisposableList<Physics2DEvent>.Create(1) };
            var contacts = DisposableList<Physics2DContactPoint>.Create(1);
            contacts.Add(default);
            original.events.Add(new Physics2DEvent { contacts = contacts });
            var copy = PurrCopy<PredictedPhysics2DData>.Copy(original);
            try
            {
                Assert.That(copy.events.list, Is.Not.SameAs(original.events.list));
                Assert.That(copy.events[0].contacts.list, Is.Not.SameAs(contacts.list));
                original.Dispose();
                Assert.That(copy.events.Count, Is.EqualTo(1));
                Assert.That(copy.events[0].contacts.Count, Is.EqualTo(1));
            }
            finally { copy.Dispose(); original.Dispose(); }
        }

        private static List<List<T>> Drain<T>()
        {
            var result = new List<List<T>>();
            int count = ListPool<T>.GetCount();
            for (int i = 0; i < count; i++) result.Add(ListPool<T>.Instantiate());
            return result;
        }
    }
}
