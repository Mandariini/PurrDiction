using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class GuaranteedRosterCounterTests
    {
        private const BindingFlags Members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Hasher.PrepareType(typeof(EmptyState));
            Hasher.PrepareType(typeof(StatefulInputProbe));
            Hasher.PrepareType(typeof(DeterministicInputProbe));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) =>
                    value.id = Packer<int>.Read(packer));
        }

        [Test]
        public void RandomizedRegistrationKeepsGuaranteedCounterInLockstepWithRoster()
        {
            var networkObject = new GameObject("Roster fuzz network");
            var managerObject = new GameObject("Roster fuzz manager");
            var slotObjects = new List<GameObject>();
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager =
                    CreateSpawnedPredictionManager(managerObject, networkManager);
                var guaranteedProperty = typeof(PredictedIdentity).GetProperty(
                    "requiresGuaranteedInputHistory", Members);
                Assert.That(guaranteedProperty, Is.Not.Null);
                var systems = GetField<List<PredictedIdentity>>(
                    typeof(PredictionManager), manager, "_systems");

                var identities = new List<PredictedIdentity>();
                var objectIds = new List<PredictedObjectID>();
                var registerable = new List<bool>();

                void AddSlot(bool deterministic, uint objectId, bool canRegister)
                {
                    var slotObject = new GameObject($"Roster slot {slotObjects.Count}");
                    slotObjects.Add(slotObject);
                    identities.Add(deterministic
                        ? slotObject.AddComponent<DeterministicInputProbe>()
                        : (PredictedIdentity)slotObject.AddComponent<StatefulInputProbe>());
                    objectIds.Add(new PredictedObjectID(objectId));
                    registerable.Add(canRegister);
                }

                AddSlot(true, 600, true);
                AddSlot(true, 601, true);
                AddSlot(false, 602, true);
                AddSlot(false, 603, true);
                AddSlot(true, 600, true);
                AddSlot(false, 602, true);
                AddSlot(true, 606, false);
                AddSlot(false, 607, false);

                int RecountGuaranteed()
                {
                    var count = 0;
                    for (var i = 0; i < systems.Count; i++)
                    {
                        if ((bool)guaranteedProperty.GetValue(systems[i]))
                            count++;
                    }
                    return count;
                }

                var rng = new System.Random(1337);
                int registers = 0;
                int duplicateRegisters = 0;
                int removals = 0;
                int guardedUnregisters = 0;

                for (var op = 0; op < 300; op++)
                {
                    var slot = rng.Next(identities.Count);
                    var identity = identities[slot];
                    bool contained = systems.Contains(identity);

                    if (registerable[slot] && rng.Next(2) == 0)
                    {
                        manager.RegisterInstance(
                            slotObjects[slot], objectIds[slot], null, false, false);
                        if (contained)
                            duplicateRegisters++;
                        else
                            registers++;
                    }
                    else
                    {
                        manager.UnregisterInstance(identity);
                        if (contained)
                            removals++;
                        else
                            guardedUnregisters++;
                    }

                    Assert.That(
                        manager.guaranteedInputHistorySystems,
                        Is.EqualTo(RecountGuaranteed()),
                        $"guaranteed counter diverged from the roster at op {op}");
                    Assert.That(
                        GetField<int>(typeof(PredictionManager), manager, "_systemsCount"),
                        Is.EqualTo(systems.Count),
                        $"systems count bookkeeping diverged at op {op}");
                }

                Assert.That(registers, Is.GreaterThan(0));
                Assert.That(duplicateRegisters, Is.GreaterThan(0));
                Assert.That(removals, Is.GreaterThan(0));
                Assert.That(guardedUnregisters, Is.GreaterThan(0));

                for (var i = 0; i < identities.Count; i++)
                    manager.UnregisterInstance(identities[i]);

                Assert.That(manager.guaranteedInputHistorySystems, Is.Zero);
                Assert.That(systems.Count, Is.Zero);
            }
            finally
            {
                for (var i = slotObjects.Count - 1; i >= 0; i--)
                    Object.DestroyImmediate(slotObjects[i]);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void LaggingBaselineTranscriptFollowsTheGuaranteedRoster()
        {
            var networkObject = new GameObject("Transcript network");
            var managerObject = new GameObject("Transcript manager");
            var guaranteedObject = new GameObject("Transcript guaranteed identity");
            var newestObject = new GameObject("Transcript newest identity");
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager =
                    CreateSpawnedPredictionManager(managerObject, networkManager);
                var guaranteed = guaranteedObject.AddComponent<DeterministicInputProbe>();
                var newest = newestObject.AddComponent<StatefulInputProbe>();
                manager.RegisterInstance(
                    guaranteedObject, new PredictedObjectID(700), null, false, false);
                manager.RegisterInstance(
                    newestObject, new PredictedObjectID(701), null, false, false);
                Assert.That(manager.guaranteedInputHistorySystems, Is.EqualTo(1));

                SetLocalTick(manager, 20);
                for (ulong tick = 16; tick <= 20; tick++)
                    SeedInput(guaranteed, tick, (int)tick);
                SeedInput(newest, 20, 777);

                using (var frame = BitPackerPool.Get())
                {
                    WriteInputHistory(manager, frame, baselineTick: 15);
                    var writtenBits = frame.positionInBits;
                    frame.ResetPositionAndMode(true);

                    PackedUInt transcriptTicks = default;
                    Packer<PackedUInt>.Read(frame, ref transcriptTicks);
                    Assert.That(transcriptTicks.value, Is.EqualTo(5),
                        "a lagging baseline with a guaranteed system must ship the transcript window");

                    for (ulong tick = 16; tick <= 20; tick++)
                    {
                        PackedUInt entryCount = default;
                        Packer<PackedUInt>.Read(frame, ref entryCount);
                        Assert.That(entryCount.value, Is.EqualTo(1),
                            $"transcript tick {tick}");
                        PredictedComponentID id = default;
                        Packer<PredictedComponentID>.Read(frame, ref id);
                        Assert.That(id, Is.EqualTo(guaranteed.id),
                            "only guaranteed systems belong in transcript blocks");
                        Assert.That(ReadLengthPrefixedInput(frame).id, Is.EqualTo((int)tick));
                    }

                    PackedUInt newestCount = default;
                    Packer<PackedUInt>.Read(frame, ref newestCount);
                    Assert.That(newestCount.value, Is.EqualTo(1));
                    PredictedComponentID newestId = default;
                    Packer<PredictedComponentID>.Read(frame, ref newestId);
                    Assert.That(newestId, Is.EqualTo(newest.id),
                        "only non-guaranteed systems belong in the newest block");
                    Assert.That(Packer<bool>.Read(frame), Is.False,
                        "no cached baseline block exists, so the repeat bit must be clear");
                    Assert.That(ReadLengthPrefixedInput(frame).id, Is.EqualTo(777));
                    Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
                }

                manager.UnregisterInstance(guaranteed);
                Assert.That(manager.guaranteedInputHistorySystems, Is.Zero);

                using (var frame = BitPackerPool.Get())
                {
                    WriteInputHistory(manager, frame, baselineTick: 15);
                    var writtenBits = frame.positionInBits;
                    frame.ResetPositionAndMode(true);

                    PackedUInt transcriptTicks = default;
                    Packer<PackedUInt>.Read(frame, ref transcriptTicks);
                    Assert.That(transcriptTicks.value, Is.Zero,
                        "an input-transcript window survived with no guaranteed systems registered");

                    PackedUInt newestCount = default;
                    Packer<PackedUInt>.Read(frame, ref newestCount);
                    Assert.That(newestCount.value, Is.EqualTo(1));
                    PredictedComponentID newestId = default;
                    Packer<PredictedComponentID>.Read(frame, ref newestId);
                    Assert.That(newestId, Is.EqualTo(newest.id));
                    Assert.That(Packer<bool>.Read(frame), Is.False);
                    Assert.That(ReadLengthPrefixedInput(frame).id, Is.EqualTo(777));
                    Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
                }
            }
            finally
            {
                Object.DestroyImmediate(newestObject);
                Object.DestroyImmediate(guaranteedObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void UnregisteringGuaranteedSystemForcesFullFramesForEveryClient()
        {
            var networkObject = new GameObject("Heal network");
            var managerObject = new GameObject("Heal manager");
            var guaranteedObject = new GameObject("Heal guaranteed identity");
            var newestObject = new GameObject("Heal newest identity");
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager =
                    CreateSpawnedPredictionManager(managerObject, networkManager);
                var guaranteed = guaranteedObject.AddComponent<DeterministicInputProbe>();
                var newest = newestObject.AddComponent<StatefulInputProbe>();
                manager.RegisterInstance(
                    guaranteedObject, new PredictedObjectID(720), null, false, false);
                manager.RegisterInstance(
                    newestObject, new PredictedObjectID(721), null, false, false);
                Assert.That(manager.guaranteedInputHistorySystems, Is.EqualTo(1));

                var clientFrames = GetField<List<PlayerPacker>>(
                    typeof(PredictionManager), manager, "_clientFrames");
                clientFrames.Add(new PlayerPacker
                {
                    player = new PlayerID(10, false),
                    packer = BitPackerPool.Get()
                });
                clientFrames.Add(new PlayerPacker
                {
                    player = new PlayerID(11, false),
                    packer = BitPackerPool.Get()
                });

                manager.UnregisterInstance(newest);
                for (var i = 0; i < clientFrames.Count; i++)
                {
                    Assert.That(clientFrames[i].fullFrame, Is.False,
                        "removing a non-guaranteed system must not force full frames");
                }

                manager.UnregisterInstance(guaranteed);
                Assert.That(manager.guaranteedInputHistorySystems, Is.Zero);
                for (var i = 0; i < clientFrames.Count; i++)
                {
                    Assert.That(clientFrames[i].fullFrame, Is.True,
                        "removing a guaranteed system must re-anchor every client with a full frame");
                }
            }
            finally
            {
                Object.DestroyImmediate(newestObject);
                Object.DestroyImmediate(guaranteedObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        private static PredictionManager CreateSpawnedPredictionManager(
            GameObject managerObject,
            NetworkManager networkManager)
        {
            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(
                typeof(NetworkIdentity),
                manager,
                "<networkManager>k__BackingField",
                networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static void SeedInput(PredictedIdentity identity, ulong tick, int value)
        {
            using var payload = BitPackerPool.Get();
            Packer<bool>.Write(payload, true);
            Packer<TrackedInput>.Write(payload, new TrackedInput(value));
            payload.ResetPositionAndMode(true);
            identity.ReadFirstInput(tick, payload);
        }

        private static void WriteInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong baselineTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteVisibilityInputHistory", Members);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                manager,
                new object[]
                {
                    default(PlayerID),
                    frame,
                    baselineTick,
                    new PlayerVisibilityTimeline()
                });
        }

        private static TrackedInput ReadLengthPrefixedInput(BitPacker frame)
        {
            PackedUInt declaredBits = default;
            Packer<PackedUInt>.Read(frame, ref declaredBits);
            var origin = frame.positionInBits;
            Assert.That(Packer<bool>.Read(frame), Is.True);
            TrackedInput input = default;
            Packer<TrackedInput>.Read(frame, ref input);
            Assert.That(frame.positionInBits - origin, Is.EqualTo((int)declaredBits.value));
            return input;
        }

        private static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, Members);
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
            var field = declaringType.GetField(fieldName, Members);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }
}
