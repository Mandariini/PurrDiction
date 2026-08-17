using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// The module section of a prediction record is framed purely by each peer's live module
    /// roster: WriteModules loops the sender's list, ReadModules loops the receiver's. Static
    /// modules are never validated against the sender, so any asymmetric registration (e.g.
    /// owner-gated RegisterModule in LateAwake) makes the receiver misparse every record for
    /// that identity. These tests pin the failure mode: a roster mismatch must surface as an
    /// actionable record failure, never as a silent misparse.
    /// </summary>
    public sealed class ModuleTopologyMismatchTests
    {
        GameObject _serverManagerGo;
        GameObject _clientManagerGo;
        GameObject _serverGo;
        GameObject _clientGo;
        PredictionManager _serverManager;
        PredictionManager _clientManager;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            PurrNet.Utils.Hasher.PrepareType(typeof(OmittedState));
            PurrNet.Utils.Hasher.PrepareType(typeof(OmittedAlphaModule));
            PurrNet.Utils.Hasher.PrepareType(typeof(OmittedBetaModule));
            Packer<OmittedState>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.value));
            Packer<OmittedState>.RegisterReader(
                (BitPacker packer, ref OmittedState value) =>
                    value.value = Packer<int>.Read(packer));
        }

        [SetUp]
        public void SetUp()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            _serverManagerGo = new GameObject("ServerManager");
            _clientManagerGo = new GameObject("ClientManager");
            _serverGo = new GameObject("ServerIdentity");
            _clientGo = new GameObject("ClientIdentity");

            _serverManager = CreateManager(_serverManagerGo);
            _clientManager = CreateManager(_clientManagerGo);
        }

        static PredictionManager CreateManager(GameObject go)
        {
            var manager = go.AddComponent<PredictionManager>();
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            SetField(typeof(PredictionManager), manager, "<localTick>k__BackingField", 1UL);
            return manager;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Object.DestroyImmediate(_serverGo);
            Object.DestroyImmediate(_clientGo);
            Object.DestroyImmediate(_serverManagerGo);
            Object.DestroyImmediate(_clientManagerGo);
        }

        (OmittedStateIdentity server, OmittedStateIdentity client) CreatePair()
        {
            var id = new PredictedComponentID(new PredictedObjectID(20), 2);

            var server = _serverGo.AddComponent<OmittedStateIdentity>();
            InitializeIdentity(server, _serverManager, id, 7);

            var client = _clientGo.AddComponent<OmittedStateIdentity>();
            InitializeIdentity(client, _clientManager, id, 0);

            return (server, client);
        }

        static void InitializeIdentity(OmittedStateIdentity identity, PredictionManager manager, PredictedComponentID id, int value)
        {
            identity.AttachForTest(manager, id);
            identity.fullPredictedState = new FULL_STATE<OmittedState>
            {
                state = new OmittedState { value = value }
            };

            var predicted = new History<FULL_STATE<OmittedState>>(200);
            predicted.Write(0, identity.fullPredictedState.DeepCopy());
            SetField(typeof(PredictedIdentity<OmittedState>), identity, "_stateHistory", predicted);

            var verified = manager.GetVerifiedHistory<FULL_STATE<OmittedState>>(id, out _);
            SetField(typeof(PredictedIdentity<OmittedState>), identity, "_verifiedHistory", verified);
        }

        static BitPacker WriteFullRecord(OmittedStateIdentity server)
        {
            var destination = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();
            server.RunWriteFirstState(1, payload);
            AddressedPredictionRecords.WriteRecord(destination, server.id, true, payload);
            destination.ResetPositionAndMode(true);
            return destination;
        }

        static void ReadFullRecord(OmittedStateIdentity client, BitPacker source)
        {
            AddressedPredictionRecords.ReadOne(
                source: source,
                readRecord: (recordId, _, payload, _) =>
                {
                    client.RunReadFirstState(1, payload, 1);
                });
        }

        [Test]
        public void MatchingStaticModulesRoundTrip()
        {
            var (server, client) = CreatePair();

            _ = new OmittedAlphaModule(server);
            _ = new OmittedAlphaModule(client);

            using var record = WriteFullRecord(server);
            Assert.DoesNotThrow(() => ReadFullRecord(client, record));
            client.RunRollback(1);
            Assert.That(client.currentState.value, Is.EqualTo(7),
                "matching rosters must decode the sender's state exactly");
        }

        [Test]
        public void ReceiverWithExtraStaticModuleFailsActionably()
        {
            var (server, client) = CreatePair();

            _ = new OmittedAlphaModule(server);
            _ = new OmittedAlphaModule(client);
            _ = new OmittedBetaModule(client);

            using var record = WriteFullRecord(server);

            var exception = Assert.Throws<InvalidOperationException>(
                () => ReadFullRecord(client, record),
                "an asymmetric static module roster must surface as a record failure");

            Assert.That(
                exception.Message,
                Does.Contain(nameof(OmittedStateIdentity)).Or.Contain("module"),
                "the failure must name the identity type or the module roster so the " +
                $"developer can act on it; got: {exception.Message}");
        }

        [Test]
        public void ReceiverMissingAStaticModuleFailsActionably()
        {
            var (server, client) = CreatePair();

            _ = new OmittedAlphaModule(server);
            _ = new OmittedBetaModule(server);
            _ = new OmittedAlphaModule(client);

            using var record = WriteFullRecord(server);

            var exception = Assert.Throws<InvalidOperationException>(
                () => ReadFullRecord(client, record),
                "a receiver missing a static module must fail loudly, not silently " +
                "decode the extra module's payload as identity state");

            Assert.That(
                exception.Message,
                Does.Contain(nameof(OmittedStateIdentity)).Or.Contain("module"),
                "the failure must name the identity type or the module roster so the " +
                $"developer can act on it; got: {exception.Message}");
        }

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }
}
