using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DesyncPolicyTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(OmittedState));
            Packer<OmittedState>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.value));
            Packer<OmittedState>.RegisterReader(
                (BitPacker packer, ref OmittedState value) =>
                    value.value = Packer<int>.Read(packer));
        }

        [Test]
        public void ResolutionUsesOverrideUnlessInherit()
        {
            Assert.That(
                DesyncPolicyResolution.Resolve(DesyncPolicyOverride.Inherit, DesyncPolicy.Correct),
                Is.EqualTo(DesyncPolicy.Correct));
            Assert.That(
                DesyncPolicyResolution.Resolve(DesyncPolicyOverride.Ignore, DesyncPolicy.Correct),
                Is.EqualTo(DesyncPolicy.Ignore));
            Assert.That(
                DesyncPolicyResolution.Resolve(DesyncPolicyOverride.Report, DesyncPolicy.Ignore),
                Is.EqualTo(DesyncPolicy.Report));
            Assert.That(
                DesyncPolicyResolution.Resolve(DesyncPolicyOverride.Resync, DesyncPolicy.Ignore),
                Is.EqualTo(DesyncPolicy.Resync));
            Assert.That(
                DesyncPolicyResolution.Resolve(DesyncPolicyOverride.Correct, DesyncPolicy.Ignore),
                Is.EqualTo(DesyncPolicy.Correct));
        }

        [Test]
        public void HashIsStableAndTickSalted()
        {
            using var packerA = BitPackerPool.Get();
            using var packerB = BitPackerPool.Get();
            Packer<int>.Write(packerA, 1234);
            Packer<int>.Write(packerB, 1234);

            var sameTickA = DeterministicStateHash.Compute(7, packerA);
            var sameTickB = DeterministicStateHash.Compute(7, packerB);
            Assert.That(sameTickA, Is.EqualTo(sameTickB));

            var otherTick = DeterministicStateHash.Compute(8, packerA);
            Assert.That(otherTick, Is.Not.EqualTo(sameTickA));

            using var packerC = BitPackerPool.Get();
            Packer<int>.Write(packerC, 1235);
            var otherState = DeterministicStateHash.Compute(7, packerC);
            Assert.That(otherState, Is.Not.EqualTo(sameTickA));
        }

        [Test]
        public void HashMasksStaleBitsPastWritePosition()
        {
            using var clean = BitPackerPool.Get();
            clean.WriteBits(0b101, 3);

            using var dirty = BitPackerPool.Get();
            dirty.WriteBits(0b101, 3);
            dirty.WriteBits(0xFF, 8);
            dirty.SetBitPosition(3);

            Assert.That(
                DeterministicStateHash.Compute(3, dirty),
                Is.EqualTo(DeterministicStateHash.Compute(3, clean)));
        }

        [Test]
        public void HashHelperCannotBeDiscoveredAsAnUnsignedLongWriter()
        {
            var method = typeof(DeterministicStateHash).GetMethod(
                nameof(DeterministicStateHash.Compute),
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(2));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(ulong)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(BitPacker)));
            Assert.That(method.ReturnType, Is.EqualTo(typeof(ushort)));

            Assert.That(Packer<ulong>.WriteFunc.Method.DeclaringType,
                Is.EqualTo(typeof(PackUIntegers)),
                "PurrDiction must not replace PurrNet's ulong writer during serializer discovery");
        }

        [Test]
        public void IdentityHashMatchesForIdenticalHistoryAndDiffersOnDivergence()
        {
            var managerObject = new GameObject("Desync hash manager");
            var serverObject = new GameObject("Desync hash server identity");
            var clientObject = new GameObject("Desync hash client identity");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var id = new PredictedComponentID(new PredictedObjectID(510), 0);

                var serverIdentity = serverObject.AddComponent<DeterministicOmittedIdentity>();
                serverIdentity.AttachForTest(manager, id);
                SetHistory(serverIdentity, 4, 100);

                var clientIdentity = clientObject.AddComponent<DeterministicOmittedIdentity>();
                clientIdentity.AttachForTest(manager, id);
                SetHistory(clientIdentity, 4, 100);

                Assert.That(serverIdentity.TryGetDeterministicStateHash(6, out var serverHash), Is.True);
                Assert.That(clientIdentity.TryGetDeterministicStateHash(6, out var clientHash), Is.True);
                Assert.That(serverHash, Is.EqualTo(clientHash));

                SetHistory(clientIdentity, 4, 101);
                Assert.That(clientIdentity.TryGetDeterministicStateHash(6, out var divergedHash), Is.True);
                Assert.That(divergedHash, Is.Not.EqualTo(serverHash));
            }
            finally
            {
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ReportPolicyRaisesEventWithoutQueuingHeal()
        {
            RunReportScenario(
                DesyncPolicy.Report,
                clientValue: 101,
                expectDetection: true,
                expectHeal: false);
        }

        [Test]
        public void MatchingHashRaisesNothing()
        {
            RunReportScenario(
                DesyncPolicy.Report,
                clientValue: 100,
                expectDetection: false,
                expectHeal: false);
        }

        [Test]
        public void CorrectPolicyQueuesHealAndGatesStaleReports()
        {
            var (manager, identity, cleanup) = CreateServerRig(DesyncPolicy.Correct);
            try
            {
                var player = new PlayerID(7, false);
                InjectVisibility(manager, player);
                SuppressDesyncNotifications(manager, player);

                int detections = 0;
                manager.onDesyncDetected += (_, _, _, _) => detections++;

                SendReport(manager, player, identity.id, tick: 10, hash: WrongHashFor(identity, 10));
                Assert.That(detections, Is.EqualTo(1));
                Assert.That(PendingHeals(manager, player), Does.Contain(identity.id));

                PendingHeals(manager, player).Clear();
                SendReport(manager, player, identity.id, tick: 11, hash: WrongHashFor(identity, 11));
                Assert.That(detections, Is.EqualTo(1),
                    "reports at or before the served heal tick must be ignored");

                SendReport(manager, player, identity.id, tick: 13, hash: WrongHashFor(identity, 13));
                Assert.That(detections, Is.EqualTo(2),
                    "reports after the served heal tick must be acted on again");
            }
            finally
            {
                cleanup();
            }
        }

        [Test]
        public void IgnorePolicySkipsComparison()
        {
            RunReportScenario(
                DesyncPolicy.Ignore,
                clientValue: 101,
                expectDetection: false,
                expectHeal: false);
        }

        private void RunReportScenario(
            DesyncPolicy policy,
            int clientValue,
            bool expectDetection,
            bool expectHeal)
        {
            var (manager, identity, cleanup) = CreateServerRig(policy);
            try
            {
                var player = new PlayerID(7, false);
                InjectVisibility(manager, player);
                SuppressDesyncNotifications(manager, player);

                int detections = 0;
                manager.onDesyncDetected += (_, _, _, _) => detections++;

                ushort reported;
                if (clientValue == 100)
                {
                    Assert.That(identity.TryGetDeterministicStateHash(10, out reported), Is.True);
                }
                else
                {
                    reported = WrongHashFor(identity, 10);
                }

                SendReport(manager, player, identity.id, tick: 10, hash: reported);

                Assert.That(detections, Is.EqualTo(expectDetection ? 1 : 0));
                var heals = PendingHeals(manager, player);
                if (expectHeal)
                    Assert.That(heals, Does.Contain(identity.id));
                else
                    Assert.That(heals == null || heals.Count == 0, Is.True);
            }
            finally
            {
                cleanup();
            }
        }

        private static (PredictionManager manager, DeterministicOmittedIdentity identity, Action cleanup)
            CreateServerRig(DesyncPolicy policy)
        {
            var managerObject = new GameObject("Desync server manager");
            var identityObject = new GameObject("Desync server identity");

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            SetField(typeof(PredictionManager), manager, "<localTick>k__BackingField", (ulong)12);

            var identity = identityObject.AddComponent<DeterministicOmittedIdentity>();
            var id = new PredictedComponentID(new PredictedObjectID(520), 0);
            identity.AttachForTest(manager, id);
            identity.resolvedDesyncPolicy = policy;
            SetHistory(identity, 4, 100);

            var systems = GetField<List<PredictedIdentity>>(
                typeof(PredictionManager), manager, "_systems");
            systems.Add(identity);
            SetField(typeof(PredictionManager), manager, "_systemsCount", systems.Count);
            var instanceMap = GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(
                typeof(PredictionManager), manager, "_instanceMap");
            instanceMap[id] = identity;

            return (manager, identity, () =>
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            });
        }

        private static void SetHistory(
            DeterministicOmittedIdentity identity,
            ulong tick,
            int value)
        {
            var history = new History<FULL_STATE<OmittedState>>(200);
            history.Write(tick, new FULL_STATE<OmittedState>
            {
                state = new OmittedState { value = value }
            });
            SetField(
                typeof(DeterministicIdentity<OmittedState>),
                identity,
                "_stateHistory",
                history);
        }

        private static ushort WrongHashFor(DeterministicOmittedIdentity identity, ulong tick)
        {
            Assert.That(identity.TryGetDeterministicStateHash(tick, out var actual), Is.True);
            return (ushort)(actual ^ 0x5A5A);
        }

        private static void SendReport(
            PredictionManager manager,
            PlayerID sender,
            PredictedComponentID id,
            ulong tick,
            ushort hash)
        {
            using var payload = BitPackerPool.Get();
            Packer<PredictedComponentID>.Write(payload, id);
            payload.WriteBits(hash, 16);
            payload.ResetPositionAndMode(true);

            var method = typeof(PredictionManager).GetMethod(
                "HandleDesyncReport",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(manager, new object[] { tick, (uint)1, payload, sender });
        }

        private static void InjectVisibility(PredictionManager manager, PlayerID player)
        {
            var visibility = GetField<Dictionary<PlayerID, PlayerVisibilityTimeline>>(
                typeof(PredictionManager), manager, "_playerVisibility");
            visibility[player] = new PlayerVisibilityTimeline();
        }

        private static void SuppressDesyncNotifications(PredictionManager manager, PlayerID player)
        {
            var cooldowns = GetField<Dictionary<PlayerID, ulong>>(
                typeof(PredictionManager), manager, "_desyncNoticeCooldownTick");
            cooldowns[player] = ulong.MaxValue;
        }

        private static HashSet<PredictedComponentID> PendingHeals(
            PredictionManager manager,
            PlayerID player)
        {
            var heals = GetField<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(
                typeof(PredictionManager), manager, "_pendingDesyncHeals");
            return heals.TryGetValue(player, out var set) ? set : null;
        }

        private static T GetField<T>(
            Type declaringType,
            object target,
            string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }

        private static void SetField(
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
}
