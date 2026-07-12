using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionPolicyContractTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(PredictedIdentityState));
        }

        [Test]
        public void DeterministicIdentityRejectsSoftCorrectionEvenWhenSubclassOptsIn()
        {
            var gameObject = new GameObject(nameof(DeterministicIdentityRejectsSoftCorrectionEvenWhenSubclassOptsIn));
            try
            {
                var identity = gameObject.AddComponent<DeterministicSoftCorrectionProbe>();

                identity.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;

                Assert.That(identity.supportsSoftCorrection, Is.True,
                    "The regression probe must exercise a deterministic subclass that opts in");
                Assert.That(identity.configuredPredictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));
                Assert.That(identity.GetResolvedPredictionPolicy(), Is.EqualTo(PredictionPolicy.FullPrediction));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void StatelessFirstStateSynchronizesCurrentOwner()
        {
            var senderObject = new GameObject("StatelessOwnerSender");
            var receiverObject = new GameObject("StatelessOwnerReceiver");
            try
            {
                var sender = senderObject.AddComponent<StatelessOwnerProbe>();
                var receiver = receiverObject.AddComponent<StatelessOwnerProbe>();
                var expected = new PlayerID(new PackedULong(42), false);
                sender.AssignOwner(expected);

                using var packer = BitPackerPool.Get();
                sender.WriteFirstStateForTest(packer);
                packer.ResetPositionAndMode(true);
                receiver.ReadFirstStateForTest(packer);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void StaticFirstStateSynchronizesCurrentOwner()
        {
            var senderObject = new GameObject("StaticOwnerSender");
            var receiverObject = new GameObject("StaticOwnerReceiver");
            try
            {
                var sender = senderObject.AddComponent<StaticPredictedIdentity>();
                var receiver = receiverObject.AddComponent<StaticPredictedIdentity>();
                var expected = new PlayerID(new PackedULong(84), false);
                sender.SetOwner(expected);

                using var packer = BitPackerPool.Get();
                sender.WriteFirstState(0, packer);
                packer.ResetPositionAndMode(true);
                receiver.ReadFirstState(0, packer);

                Assert.That(receiver.owner, Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void StaticCurrentStateSynchronizesPostSpawnOwnerChange()
        {
            var senderObject = new GameObject("StaticOwnerDeltaSender");
            var receiverObject = new GameObject("StaticOwnerDeltaReceiver");
            try
            {
                var sender = senderObject.AddComponent<StaticPredictedIdentity>();
                var receiver = receiverObject.AddComponent<StaticPredictedIdentity>();
                var receiverPlayer = new PlayerID(new PackedULong(7), false);
                var expectedOwner = new PlayerID(new PackedULong(126), false);
                CreateDeltaModules(receiverPlayer, out var sendingDeltas, out var receivingDeltas);

                using (var initialPacker = BitPackerPool.Get())
                {
                    Assert.That(sender.WriteCurrentState(receiverPlayer, initialPacker, sendingDeltas), Is.False);
                    initialPacker.ResetPositionAndMode(true);
                    receiver.ReadState(9, initialPacker, receivingDeltas);
                    Assert.That(receiver.owner, Is.Null);
                }

                sender.SetOwner(expectedOwner);

                using var packer = BitPackerPool.Get();
                Assert.That(sender.WriteCurrentState(receiverPlayer, packer, sendingDeltas), Is.True);
                packer.ResetPositionAndMode(true);
                receiver.ReadState(10, packer, receivingDeltas);

                Assert.That(receiver.owner, Is.EqualTo(expectedOwner));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void StatelessCurrentStateSynchronizesPostSpawnOwnerChange()
        {
            var senderObject = new GameObject("StatelessOwnerDeltaSender");
            var receiverObject = new GameObject("StatelessOwnerDeltaReceiver");
            try
            {
                var sender = senderObject.AddComponent<StatelessOwnerProbe>();
                var receiver = receiverObject.AddComponent<StatelessOwnerProbe>();
                var receiverPlayer = new PlayerID(new PackedULong(8), false);
                var expectedOwner = new PlayerID(new PackedULong(127), false);
                CreateDeltaModules(receiverPlayer, out var sendingDeltas, out var receivingDeltas);

                sender.AssignOwner(expectedOwner);

                using var packer = BitPackerPool.Get();
                Assert.That(sender.WriteCurrentState(receiverPlayer, packer, sendingDeltas), Is.True);
                packer.ResetPositionAndMode(true);
                receiver.ReadState(10, packer, receivingDeltas);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expectedOwner));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void DeterministicCurrentStateAppliesPostSpawnOwnerAtVerifiedRollback()
        {
            var managerObject = new GameObject("DeterministicOwnerManager");
            var senderObject = new GameObject("DeterministicOwnerDeltaSender");
            var receiverObject = new GameObject("DeterministicOwnerDeltaReceiver");
            try
            {
                const ulong verifiedTick = 10;
                var manager = managerObject.AddComponent<PredictionManager>();
                var sender = senderObject.AddComponent<DeterministicOwnerProbe>();
                var receiver = receiverObject.AddComponent<DeterministicOwnerProbe>();
                var receiverPlayer = new PlayerID(new PackedULong(9), false);
                var expectedOwner = new PlayerID(new PackedULong(128), false);
                CreateDeltaModules(receiverPlayer, out var sendingDeltas, out var receivingDeltas);
                sender.AttachForTest(manager);
                receiver.AttachForTest(manager);
                SeedDeterministicHistory(receiver, verifiedTick - 1);
                sender.AssignOwner(expectedOwner);

                using var packer = BitPackerPool.Get();
                Assert.That(sender.WriteCurrentState(receiverPlayer, packer, sendingDeltas), Is.True);
                packer.ResetPositionAndMode(true);

                receiver.ClearFuture(verifiedTick);
                receiver.ReadState(verifiedTick, packer, receivingDeltas);

                Assert.That(receiver.assignedOwner, Is.Null,
                    "Verified deterministic metadata must remain in history until rollback applies its tick");

                receiver.Rollback(verifiedTick);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expectedOwner));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void LegacyProtectedPredictionKeyRemainsAvailableToCustomModules()
        {
            Assert.That(typeof(LegacyPredictionKeyModule).GetProperty(
                nameof(LegacyPredictionKeyModule.legacyPredictionKey)), Is.Not.Null);
        }

        private static void CreateDeltaModules(
            PlayerID localPlayer,
            out DeltaModule sending,
            out DeltaModule receiving)
        {
#pragma warning disable SYSLIB0050
            var players = (PlayersManager)FormatterServices.GetUninitializedObject(typeof(PlayersManager));
#pragma warning restore SYSLIB0050
            var localPlayerField = typeof(PlayersManager).GetField(
                "<localPlayerId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(localPlayerField, Is.Not.Null);
            localPlayerField.SetValue(players, (PlayerID?)localPlayer);

            var broadcaster = new PlayersBroadcaster(null, players);
            sending = new DeltaModule(players, broadcaster);
            receiving = new DeltaModule(players, broadcaster);
        }

        private static void SeedDeterministicHistory(DeterministicOwnerProbe identity, ulong tick)
        {
            var history = new History<FULL_STATE<PolicyContractState>>(32);
            history.Write(tick, identity.fullPredictedState.DeepCopy());
            var historyField = typeof(DeterministicIdentity<PolicyContractState>).GetField(
                "_stateHistory",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(historyField, Is.Not.Null);
            historyField.SetValue(identity, history);
        }
    }

    public struct PolicyContractState : IPredictedData<PolicyContractState>
    {
        public void Dispose() { }
    }

    public sealed class DeterministicSoftCorrectionProbe : DeterministicIdentity<PolicyContractState>
    {
        public override bool supportsSoftCorrection => true;
    }

    public sealed class DeterministicOwnerProbe : DeterministicIdentity<PolicyContractState>
    {
        public PlayerID? assignedOwner => ((PredictedIdentity)this).owner;

        public void AttachForTest(PredictionManager manager) => predictionManager = manager;

        public void AssignOwner(PlayerID? player) => SetOwner(player);
    }

    public sealed class StatelessOwnerProbe : StatelessPredictedIdentity
    {
        public PlayerID? assignedOwner => ((PredictedIdentity)this).owner;

        public void AssignOwner(PlayerID? player) => SetOwner(player);

        public void WriteFirstStateForTest(BitPacker packer) => WriteFirstState(0, packer);

        public void ReadFirstStateForTest(BitPacker packer) => ReadFirstState(0, packer);
    }

    public sealed class LegacyPredictionKeyModule : PredictedModule<PolicyContractState>
    {
        public LegacyPredictionKeyModule(PredictedIdentity identity) : base(identity) { }

        public ModuleDeltaKey<PredictedIdentityState> legacyPredictionKey => predictionKey;
    }
}
