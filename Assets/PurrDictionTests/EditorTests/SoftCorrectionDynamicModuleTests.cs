using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;

namespace PurrNet.Prediction.Tests.Editor
{
    public class SoftCorrectionDynamicModuleTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(SoftTopologyState));
            Packer<SoftTopologyState>.RegisterWriter((packer, value) =>
                packer.Write(value.value));
            Packer<SoftTopologyState>.RegisterReader((BitPacker packer, ref SoftTopologyState value) =>
                packer.Read(ref value.value));
        }

        [Test]
        public void PastEmptyTopologyDoesNotRemoveLiveSoftCorrectionModule()
        {
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(PastEmptyTopologyDoesNotRemoveLiveSoftCorrectionModule));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                var liveModule = new SoftTopologyAlphaModule(identity);
                liveModule.currentState.value = 73;
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(0);

                try
                {
                    identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative);

                    Assert.That(identity.modules, Is.Empty,
                        "The module read view did not match the sender's empty topology");
                    Assert.That(liveModule.disposed, Is.False,
                        "Presenting an older topology disposed a later live module");

                    identity.EndSoftCorrectionDynamicModuleRead();

                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.SameAs(liveModule));
                    Assert.That(liveModule.disposed, Is.False,
                        "Restoring the live timeline recreated or disposed its module");
                    Assert.That(liveModule.currentState.value, Is.EqualTo(73),
                        "Reading a past topology changed the later live module's state");
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void AuthoritativeReadViewUsesSenderOrderThenRestoresLiveOrder()
        {
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(AuthoritativeReadViewUsesSenderOrderThenRestoresLiveOrder));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                var alpha = new SoftTopologyAlphaModule(identity);
                var beta = new SoftTopologyBetaModule(identity);
                alpha.currentState.value = 91;
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(1);
                authoritative.Add(beta.typeHash);

                try
                {
                    identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative);

                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.SameAs(beta),
                        "The read view did not use the sender's module count and order");
                    Assert.That(beta.moduleIndex, Is.Zero,
                        "The read view did not expose the sender's delta key index");
                    Assert.That(alpha.disposed, Is.False);

                    identity.EndSoftCorrectionDynamicModuleRead();

                    Assert.That(identity.modules.Count, Is.EqualTo(2));
                    Assert.That(identity.modules[0], Is.SameAs(alpha));
                    Assert.That(identity.modules[1], Is.SameAs(beta));
                    Assert.That(alpha.moduleIndex, Is.Zero);
                    Assert.That(beta.moduleIndex, Is.EqualTo(1));
                    Assert.That(alpha.disposed, Is.False);
                    Assert.That(beta.disposed, Is.False);
                    Assert.That(alpha.currentState.value, Is.EqualTo(91),
                        "The later-only module lost state while excluded from the authoritative payload view");
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ModulePayloadUsesAuthoritativeOrderWithoutChangingLaterLiveModule()
        {
            var senderManagerObject = new GameObject("SenderPredictionManager");
            var receiverManagerObject = new GameObject("ReceiverPredictionManager");
            var senderObject = new GameObject("SoftTopologySender");
            var receiverObject = new GameObject("SoftTopologyReceiver");

            try
            {
                var sender = CreateIdentity(senderManagerObject, senderObject);
                var senderBeta = new SoftTopologyBetaModule(sender);
                senderBeta.currentState.value = 44;
                MarkAllModulesDynamic(sender);
                InitializeModuleHistory(sender);

                var receiver = CreateIdentity(receiverManagerObject, receiverObject);
                var laterOnlyAlpha = new SoftTopologyAlphaModule(receiver);
                laterOnlyAlpha.currentState.value = 91;
                var receiverBeta = new SoftTopologyBetaModule(receiver);
                receiverBeta.currentState.value = 7;
                MarkAllModulesDynamic(receiver);
                InitializeModuleHistory(receiver);

                var writerDelta = new DeltaModule(null, null);
#pragma warning disable SYSLIB0050
                var receiverPlayers = (PlayersManager)FormatterServices.GetUninitializedObject(
                    typeof(PlayersManager));
#pragma warning restore SYSLIB0050
                var readerDelta = new DeltaModule(receiverPlayers, null);

                using var packer = BitPackerPool.Get();
                sender.WriteModulePayloadForTest(packer, writerDelta);
                Packer<int>.Write(packer, 123456789);
                int writtenBits = packer.positionInBits;

                packer.ResetPositionAndMode(true);
                receiver.ReadModulePayloadForTest(10, packer, readerDelta);
                int tailValue = Packer<int>.Read(packer);

                Assert.That(packer.positionInBits, Is.EqualTo(writtenBits),
                    "The receiver did not consume exactly the sender's authoritative module payload");
                Assert.That(tailValue, Is.EqualTo(123456789),
                    "A module count or delta-key mismatch consumed the payload tail");
                Assert.That(receiver.modules.Count, Is.EqualTo(2));
                Assert.That(receiver.modules[0], Is.SameAs(laterOnlyAlpha));
                Assert.That(receiver.modules[1], Is.SameAs(receiverBeta));
                Assert.That(laterOnlyAlpha.currentState.value, Is.EqualTo(91),
                    "The later-only live module was modified while decoding the past payload");
                Assert.That(receiverBeta.currentState.value, Is.EqualTo(44),
                    "The matching live module did not consume its authoritative state");
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void RecreatedSameTypeModuleDoesNotConsumeOldLifetimePayload()
        {
            SoftTopologyBetaModule.disposeCount = 0;
            var senderManagerObject = new GameObject("SenderPredictionManager");
            var receiverManagerObject = new GameObject("ReceiverPredictionManager");
            var senderObject = new GameObject("OldLifetimeSender");
            var receiverObject = new GameObject("NewLifetimeReceiver");

            try
            {
                var sender = CreateIdentity(senderManagerObject, senderObject);
                var oldLifetime = new SoftTopologyBetaModule(sender);
                oldLifetime.currentState.value = 44;
                MarkAllModulesDynamic(sender);
                InitializeModuleHistory(sender);

                var receiver = CreateIdentity(receiverManagerObject, receiverObject);
                var newLifetime = new SoftTopologyBetaModule(receiver);
                newLifetime.currentState.value = 99;
                newLifetime.registeredAtTick = 20;
                MarkAllModulesDynamic(receiver);
                InitializeModuleHistory(receiver);

                var writerDelta = new DeltaModule(null, null);
#pragma warning disable SYSLIB0050
                var receiverPlayers = (PlayersManager)FormatterServices.GetUninitializedObject(
                    typeof(PlayersManager));
#pragma warning restore SYSLIB0050
                var readerDelta = new DeltaModule(receiverPlayers, null);

                using var packer = BitPackerPool.Get();
                sender.WriteModulePayloadForTest(packer, writerDelta);
                Packer<int>.Write(packer, 987654321);
                int writtenBits = packer.positionInBits;

                packer.ResetPositionAndMode(true);
                receiver.ReadModulePayloadForTest(10, packer, readerDelta);
                int tailValue = Packer<int>.Read(packer);

                Assert.That(packer.positionInBits, Is.EqualTo(writtenBits));
                Assert.That(tailValue, Is.EqualTo(987654321));
                Assert.That(receiver.modules.Count, Is.EqualTo(1));
                Assert.That(receiver.modules[0], Is.SameAs(newLifetime),
                    "The later same-type instance was replaced while reading the old lifetime");
                Assert.That(newLifetime.currentState.value, Is.EqualTo(99),
                    "The later same-type instance consumed the old lifetime's authoritative state");
                Assert.That(SoftTopologyBetaModule.disposeCount, Is.EqualTo(1),
                    "The old-lifetime payload was not consumed by a temporary decoder module");
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void PastOnlyModuleIsDisposedAfterAuthoritativePayloadRead()
        {
            SoftTopologyTemporaryModule.disposeCount = 0;
            SoftTopologyTemporaryModule.disposeTarget = null;
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(PastOnlyModuleIsDisposedAfterAuthoritativePayloadRead));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(1);
                authoritative.Add(Hasher.PrepareType(typeof(SoftTopologyTemporaryModule)));

                try
                {
                    identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative);

                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.TypeOf<SoftTopologyTemporaryModule>(),
                        "A module present only in the authoritative past was not created for payload decoding");

                    identity.EndSoftCorrectionDynamicModuleRead();

                    Assert.That(identity.modules, Is.Empty,
                        "A past-only module leaked into the live topology");
                    Assert.That(SoftTopologyTemporaryModule.disposeCount, Is.EqualTo(1),
                        "The temporary authoritative module did not release its state");
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void TemporaryDisposalCallbackCannotRemoveSavedLiveModule()
        {
            SoftTopologyTemporaryModule.disposeCount = 0;
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(TemporaryDisposalCallbackCannotRemoveSavedLiveModule));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                var liveModule = new SoftTopologyAlphaModule(identity);
                MarkAllModulesDynamic(identity);
                SoftTopologyTemporaryModule.disposeTarget = liveModule;
                var authoritative = DisposableList<uint>.Create(1);
                authoritative.Add(Hasher.PrepareType(typeof(SoftTopologyTemporaryModule)));

                try
                {
                    identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative);
                    identity.EndSoftCorrectionDynamicModuleRead();

                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.SameAs(liveModule));
                    Assert.That(liveModule.disposed, Is.False,
                        "A temporary module disposal callback removed a saved live module");
                    Assert.That(SoftTopologyTemporaryModule.disposeCount, Is.EqualTo(1));
                }
                finally
                {
                    SoftTopologyTemporaryModule.disposeTarget = null;
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ThrowingTemporarySetupDoesNotLeakDecoderModule()
        {
            SoftTopologyThrowingSetupModule.disposeCount = 0;
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(ThrowingTemporarySetupDoesNotLeakDecoderModule));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(1);
                authoritative.Add(Hasher.PrepareType(typeof(SoftTopologyThrowingSetupModule)));

                try
                {
                    Assert.Throws<System.InvalidOperationException>(() =>
                        identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative));
                    Assert.That(identity.modules, Is.Empty);
                    Assert.That(SoftTopologyThrowingSetupModule.disposeCount, Is.EqualTo(1),
                        "A decoder module whose setup failed did not release its initialized state");
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ThrowingTemporaryDisposalStillCleansRemainingDecoders()
        {
            SoftTopologyTemporaryModule.disposeCount = 0;
            SoftTopologyTemporaryModule.disposeTarget = null;
            SoftTopologyThrowingDisposeModule.disposeCount = 0;
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(ThrowingTemporaryDisposalStillCleansRemainingDecoders));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                var liveModule = new SoftTopologyAlphaModule(identity);
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(2);
                authoritative.Add(Hasher.PrepareType(typeof(SoftTopologyTemporaryModule)));
                authoritative.Add(Hasher.PrepareType(typeof(SoftTopologyThrowingDisposeModule)));

                try
                {
                    identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative);
                    Assert.Throws<System.InvalidOperationException>(() =>
                        identity.EndSoftCorrectionDynamicModuleRead());

                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.SameAs(liveModule));
                    Assert.That(liveModule.disposed, Is.False);
                    Assert.That(SoftTopologyThrowingDisposeModule.disposeCount, Is.EqualTo(1));
                    Assert.That(SoftTopologyTemporaryModule.disposeCount, Is.EqualTo(1),
                        "Cleanup stopped after the first temporary module threw during disposal");
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void UnconstructableAuthoritativeModuleAbortsAndRestoresLiveTopology()
        {
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(UnconstructableAuthoritativeModuleAbortsAndRestoresLiveTopology));

            try
            {
                var identity = CreateIdentity(managerObject, identityObject);
                var liveModule = new SoftTopologyAlphaModule(identity);
                MarkAllModulesDynamic(identity);
                var authoritative = DisposableList<uint>.Create(1);
                const uint unknownTypeHash = 0xDEADBEEF;
                Assert.That(Hasher.TryGetType(unknownTypeHash, out _), Is.False,
                    "The test hash unexpectedly resolves to a registered module type");
                authoritative.Add(unknownTypeHash);

                try
                {
                    LogAssert.Expect(LogType.Error,
                        new Regex("Dynamic module reconcile failed.*3735928559"));
                    Assert.Throws<System.InvalidOperationException>(() =>
                        identity.BeginSoftCorrectionDynamicModuleRead(10, authoritative));
                    Assert.That(identity.modules.Count, Is.EqualTo(1));
                    Assert.That(identity.modules[0], Is.SameAs(liveModule));
                    Assert.That(liveModule.disposed, Is.False);
                }
                finally
                {
                    identity.EndSoftCorrectionDynamicModuleRead();
                    authoritative.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static SoftTopologyIdentity CreateIdentity(GameObject managerObject, GameObject identityObject)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            typeof(PredictionManager).GetField("<tickRate>k__BackingField", InstanceFields)
                ?.SetValue(manager, 20);

            var identity = identityObject.AddComponent<SoftTopologyIdentity>();
            identity.AttachForTest(manager);
            identity.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);
            return identity;
        }

        private static void MarkAllModulesDynamic(PredictedIdentity identity)
        {
            typeof(PredictedIdentity).GetField("_staticModuleCount", InstanceFields)
                ?.SetValue(identity, 0);
        }

        private static void InitializeModuleHistory(PredictedIdentity identity)
        {
            typeof(PredictedIdentity).GetField("_moduleHistory", InstanceFields)
                ?.SetValue(identity, new History<DisposableList<uint>>(200));
        }
    }

    public sealed class SoftTopologyIdentity : PredictedIdentity<SoftTopologyState>
    {
        public override bool supportsSoftCorrection => true;

        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
        }

        public void WriteModulePayloadForTest(BitPacker packer, DeltaModule deltaModule)
        {
            WriteDynamicModuleSnapshot(default, packer, deltaModule);
            WriteModules(default, packer, deltaModule);
        }

        public void ReadModulePayloadForTest(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            ReadDynamicModuleSnapshot(tick, packer, deltaModule);
            ReadModules(tick, packer, deltaModule);
        }
    }

    public struct SoftTopologyState : IPredictedData<SoftTopologyState>
    {
        public int value;

        public void Dispose() { }
    }

    public abstract class SoftTopologyModule : PredictedModule<SoftTopologyState>
    {
        public bool disposed { get; private set; }

        protected SoftTopologyModule(PredictedIdentity identity) : base(identity) { }

        protected override void OnDisposed()
        {
            disposed = true;
            base.OnDisposed();
        }
    }

    public sealed class SoftTopologyAlphaModule : SoftTopologyModule
    {
        public SoftTopologyAlphaModule(PredictedIdentity identity) : base(identity) { }
    }

    public sealed class SoftTopologyBetaModule : SoftTopologyModule
    {
        public static int disposeCount;

        public SoftTopologyBetaModule(PredictedIdentity identity) : base(identity) { }

        protected override void OnDisposed()
        {
            disposeCount++;
            base.OnDisposed();
        }
    }

    public sealed class SoftTopologyTemporaryModule : SoftTopologyModule
    {
        public static int disposeCount;
        public static PredictedModule disposeTarget;

        public SoftTopologyTemporaryModule(PredictedIdentity identity) : base(identity) { }

        protected override void OnDisposed()
        {
            disposeCount++;
            disposeTarget?.Dispose();
            base.OnDisposed();
        }
    }

    public sealed class SoftTopologyThrowingSetupModule : SoftTopologyModule
    {
        public static int disposeCount;

        public SoftTopologyThrowingSetupModule(PredictedIdentity identity) : base(identity) { }

        protected override void Setup(PredictedIdentity parent, PredictionManager world)
        {
            throw new System.InvalidOperationException("setup failed");
        }

        protected override void OnDisposed()
        {
            disposeCount++;
            base.OnDisposed();
        }
    }

    public sealed class SoftTopologyThrowingDisposeModule : SoftTopologyModule
    {
        public static int disposeCount;

        public SoftTopologyThrowingDisposeModule(PredictedIdentity identity) : base(identity) { }

        protected override void OnDisposed()
        {
            disposeCount++;
            base.OnDisposed();
            throw new System.InvalidOperationException("dispose failed");
        }
    }
}
