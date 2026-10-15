using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class AuthoritativeHistoryCoalescingTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [Test]
        public void PlayerRosterComparisonIgnoresStoragePadding()
        {
            var ids = new[] { new PlayerID(7, false), new PlayerID(7, false) };
            SetPlayerPadding(ids);
            ref var firstId = ref ids[0];
            ref var secondId = ref ids[1];
            using var firstBits = BitPackerPool.Get();
            using var secondBits = BitPackerPool.Get();
            Packer<PlayerID>.Write(firstBits, firstId);
            Packer<PlayerID>.Write(secondBits, secondId);
            Assert.That(new BitData(firstBits, 0, firstBits.positionInBits).Equals(
                new BitData(secondBits, 0, secondBits.positionInBits)), Is.True);
            Assert.That(Packer.AreEqualRef(ref firstId, ref secondId), Is.True,
                "storage padding must not make identical player IDs look like changed wire state");

            using var firstPlayers = DisposableList<PlayerID>.Create(1);
            using var secondPlayers = DisposableList<PlayerID>.Create(1);
            firstPlayers.Add(firstId);
            secondPlayers.Add(secondId);
            var first = new FULL_STATE<PredictedPlayersState>
                { state = new PredictedPlayersState { players = firstPlayers } };
            var second = new FULL_STATE<PredictedPlayersState>
                { state = new PredictedPlayersState { players = secondPlayers } };
            var history = History<FULL_STATE<PredictedPlayersState>>.ForTickWindow(32);
            try
            {
                Assert.That(VerifiedStateStore<FULL_STATE<PredictedPlayersState>>.StoreLive(
                    history, 1, ref first, true), Is.True);
                Assert.That(VerifiedStateStore<FULL_STATE<PredictedPlayersState>>.StoreLive(
                    history, 2, ref second, true), Is.False);
                Assert.That(history.Count, Is.EqualTo(1));
            }
            finally { history.Clear(); }
            Assert.That(firstPlayers.isDisposed || secondPlayers.isDisposed, Is.False);
        }

        private static void SetPlayerPadding(PlayerID[] values)
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(values,
                System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var first = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(values, 0);
                var second = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(values, 1);
                int stride = checked((int)(second.ToInt64() - first.ToInt64()));
                for (int i = sizeof(ulong) + sizeof(byte); i < stride; i++)
                {
                    System.Runtime.InteropServices.Marshal.WriteByte(first, i, 0x35);
                    System.Runtime.InteropServices.Marshal.WriteByte(second, i, 0xa7);
                }
            }
            finally { pin.Free(); }
        }

        [Test]
        public void EqualPersistentStateKeepsItsAnchorAndDisposesTheReceivedCopy()
        {
            using var fixture = new Fixture();
            var copyDelegate = PurrCopy<PredictedHierarchyState>.Copy;
            PredictedHierarchyState received = default;
            try
            {
                PurrCopy<PredictedHierarchyState>.Override((in PredictedHierarchyState state) =>
                {
                    received = state.Duplicate();
                    return received;
                });
                for (ulong tick = 11; tick <= 200; tick++)
                {
                    fixture.identity.ReadUnchangedState(tick, 10, tick);
                    Assert.That(received.spawnedPrefabs.isDisposed, Is.True);
                    Assert.That(received.toDelete.isDisposed, Is.True);
                }
                Assert.That(fixture.predicted.Count, Is.EqualTo(1));
                Assert.That(fixture.predicted.ReadOrPrevious(200, out var anchor), Is.True);
                Assert.That(anchor.state.spawnedPrefabs.isDisposed, Is.False);
                Assert.That(anchor.state.spawnedPrefabs.Count, Is.EqualTo(1));
                Assert.That(fixture.verified[0].state.spawnedPrefabs.isDisposed, Is.False);
                fixture.identity.Rollback(200);
                Assert.That(fixture.identity.currentState.nextInstanceId, Is.EqualTo(100));
            }
            finally
            {
                PurrCopy<PredictedHierarchyState>.Override(copyDelegate);
            }
        }

        [Test]
        public void InterveningChangesAndOlderWritesKeepTheirOwnEntries()
        {
            using var fixture = new Fixture();
            fixture.predicted.Write(11, State(200));
            fixture.identity.ReadUnchangedState(12, 10, 12);
            Assert.That(fixture.predicted.Read(12, out var restored), Is.True);
            Assert.That(restored.state.nextInstanceId, Is.EqualTo(100));
            Assert.That(fixture.predicted.Read(11, out var changed), Is.True);
            Assert.That(changed.state.nextInstanceId, Is.EqualTo(200));

            // An equal latest entry cannot stand in for a write earlier in the timeline.
            fixture.identity.ReadUnchangedState(9, 10, 13);
            Assert.That(fixture.predicted.Read(9, out var older), Is.True);
            Assert.That(older.state.nextInstanceId, Is.EqualTo(100));
            fixture.identity.Rollback(11);
            Assert.That(fixture.identity.currentState.nextInstanceId, Is.EqualTo(200));
        }

        [Test]
        public void EqualEventBatchesRetainAnExactEntryForEveryTick()
        {
            using var fixture = new Fixture(events: true);
            for (ulong tick = 11; tick <= 213; tick++)
            {
                fixture.identity.ReadUnchangedState(tick, tick - 1, tick);
                Assert.That(fixture.predicted.Read(tick, out var batch), Is.True);
                Assert.That(batch.state.spawnedPrefabs.isDisposed, Is.False);
                Assert.That(fixture.identity.TryGetExactVerifiedState(tick, out _), Is.True);
            }
            Assert.That(fixture.predicted.Count, Is.EqualTo(201));
            Assert.That(fixture.predicted.OldestTick, Is.EqualTo(13UL));
            Assert.That(fixture.predicted.Read(12, out _), Is.False);
        }

        private static FULL_STATE<PredictedHierarchyState> State(uint nextInstanceId)
        {
            var records = DisposableList<InstanceDetails>.Create(1);
            records.Add(new InstanceDetails(0, new PredictedObjectID(2),
                Vector3.zero, Quaternion.identity, null));
            return new FULL_STATE<PredictedHierarchyState>
            {
                state = new PredictedHierarchyState(records,
                    DisposableList<PredictedObjectID>.Create(0), nextInstanceId)
            };
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject managerObject = new("Authoritative history manager");
            private readonly GameObject identityObject = new("Authoritative history identity");
            public readonly AuthoritativeHistoryIdentity identity;
            public readonly History<FULL_STATE<PredictedHierarchyState>> predicted = new(200);
            public readonly History<FULL_STATE<PredictedHierarchyState>> verified;

            public Fixture(bool events = false)
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                identity = events
                    ? identityObject.AddComponent<AuthoritativeEventHistoryIdentity>()
                    : identityObject.AddComponent<AuthoritativeHistoryIdentity>();
                identity.id = new PredictedComponentID(new PredictedObjectID(2), 0);
                Set(typeof(PredictedIdentity), identity, "<predictionManager>k__BackingField", manager);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                verified = manager.GetVerifiedHistory<FULL_STATE<PredictedHierarchyState>>(identity.id, out _);
                Set(typeof(PredictedIdentity<PredictedHierarchyState>), identity, "_stateHistory", predicted);
                Set(typeof(PredictedIdentity<PredictedHierarchyState>), identity, "_verifiedHistory", verified);
                predicted.Write(10, State(100));
                verified.Write(10, State(100));
            }

            public void Dispose()
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }

            private static void Set(Type type, object instance, string name, object value)
                => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(instance, value);
        }
    }

    public class AuthoritativeHistoryIdentity : PredictedIdentity<PredictedHierarchyState> { }

    public sealed class AuthoritativeEventHistoryIdentity : AuthoritativeHistoryIdentity
    {
        internal override bool isEventHandler => true;
    }
}
