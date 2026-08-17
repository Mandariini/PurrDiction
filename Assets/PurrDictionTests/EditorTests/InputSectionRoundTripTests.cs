using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InputSectionRoundTripTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(TrackedInput));
            Packer<TrackedInput>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.id));
            Packer<TrackedInput>.RegisterReader(
                (BitPacker packer, ref TrackedInput value) =>
                    value.id = Packer<int>.Read(packer));
        }

        [Test]
        public void MixedRosterLaggingBaselineRoundTripsTranscriptAndNewestBlocks()
        {
            var senderManagerObject = new GameObject("Input rt sender manager");
            var receiverManagerObject = new GameObject("Input rt receiver manager");
            var senderGuaranteedObject = new GameObject("Input rt sender guaranteed");
            var senderNewestObject = new GameObject("Input rt sender newest");
            var receiverGuaranteedObject = new GameObject("Input rt receiver guaranteed");
            var receiverNewestObject = new GameObject("Input rt receiver newest");
            try
            {
                var guaranteedId = new PredictedComponentID(new PredictedObjectID(600), 0);
                var newestId = new PredictedComponentID(new PredictedObjectID(601), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddGuaranteedInputProbe(
                    senderGuaranteedObject, senderManager, guaranteedId,
                    out var senderGuaranteedHistory);
                AddNewestOnlyInputProbe(
                    senderNewestObject, senderManager, newestId,
                    out var senderNewestHistory);

                for (ulong t = 13; t <= 20; t++)
                {
                    if (t != 15)
                        senderGuaranteedHistory.Write(t, new TrackedInput((int)(100 + t)));
                    senderNewestHistory.Write(
                        t, new TrackedInput(t is 16 or 20 ? 555 : (int)(200 + t)));
                }

                var receiverManager = CreateManager(receiverManagerObject);
                AddGuaranteedInputProbe(
                    receiverGuaranteedObject, receiverManager, guaranteedId,
                    out var receiverGuaranteedHistory);
                AddNewestOnlyInputProbe(
                    receiverNewestObject, receiverManager, newestId,
                    out var receiverNewestHistory);

                SetLocalTick(senderManager, 16);
                using (var frameA = BitPackerPool.Get())
                {
                    WriteVisibilityInputHistory(senderManager, frameA, baselineTick: 13);
                    int writtenBits = frameA.positionInBits;

                    frameA.ResetPositionAndMode(true);
                    ReadInputHistory(receiverManager, frameA, serverTick: 16, baselineTick: 13);

                    Assert.That(frameA.positionInBits, Is.EqualTo(writtenBits));
                    AssertInput(receiverGuaranteedHistory, 14, 114);
                    AssertInput(receiverGuaranteedHistory, 16, 116);
                    Assert.That(receiverGuaranteedHistory.TryGet(13, out _), Is.False,
                        "the baseline tick itself must not ride the transcript");
                    Assert.That(receiverGuaranteedHistory.TryGet(15, out _), Is.False,
                        "a tick without source input must decode as an empty block");
                    AssertInput(receiverNewestHistory, 16, 555);
                    Assert.That(receiverNewestHistory.TryGet(14, out _), Is.False,
                        "non-guaranteed systems must not appear in transcript ticks");
                }

                SetLocalTick(senderManager, 20);
                using var frameB = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, frameB, baselineTick: 16);
                int writtenBitsB = frameB.positionInBits;

                frameB.ResetPositionAndMode(true);
                Assert.That(ReadPackedUInt(frameB), Is.EqualTo(4));
                for (var k = 0; k < 4; k++)
                {
                    Assert.That(ReadPackedUInt(frameB), Is.EqualTo(1));
                    PredictedComponentID pid = default;
                    Packer<PredictedComponentID>.Read(frameB, ref pid);
                    Assert.That(pid, Is.EqualTo(guaranteedId));
                    var payloadBits = ReadPackedUInt(frameB);
                    Assert.That(payloadBits, Is.GreaterThan(0));
                    frameB.SkipBits((int)payloadBits);
                }

                Assert.That(ReadPackedUInt(frameB), Is.EqualTo(1));
                PredictedComponentID newestPid = default;
                Packer<PredictedComponentID>.Read(frameB, ref newestPid);
                Assert.That(newestPid, Is.EqualTo(newestId));
                Assert.That(Packer<bool>.Read(frameB), Is.True,
                    "an unchanged newest payload against the acked baseline must ride the repeat bit");
                Assert.That(frameB.positionInBits, Is.EqualTo(writtenBitsB));

                frameB.ResetPositionAndMode(true);
                ReadInputHistory(receiverManager, frameB, serverTick: 20, baselineTick: 16);

                Assert.That(frameB.positionInBits, Is.EqualTo(writtenBitsB));
                for (ulong t = 17; t <= 20; t++)
                    AssertInput(receiverGuaranteedHistory, t, (int)(100 + t));
                AssertInput(receiverNewestHistory, 20, 555);
            }
            finally
            {
                Object.DestroyImmediate(receiverNewestObject);
                Object.DestroyImmediate(receiverGuaranteedObject);
                Object.DestroyImmediate(senderNewestObject);
                Object.DestroyImmediate(senderGuaranteedObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void ReadConsumesExactlyTheWrittenBitCount()
        {
            var senderManagerObject = new GameObject("Input framing sender manager");
            var receiverManagerObject = new GameObject("Input framing receiver manager");
            var senderGuaranteedObject = new GameObject("Input framing sender guaranteed");
            var senderNewestObject = new GameObject("Input framing sender newest");
            var receiverGuaranteedObject = new GameObject("Input framing receiver guaranteed");
            var receiverNewestObject = new GameObject("Input framing receiver newest");
            try
            {
                var guaranteedId = new PredictedComponentID(new PredictedObjectID(610), 0);
                var newestId = new PredictedComponentID(new PredictedObjectID(611), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddGuaranteedInputProbe(
                    senderGuaranteedObject, senderManager, guaranteedId,
                    out var senderGuaranteedHistory);
                AddNewestOnlyInputProbe(
                    senderNewestObject, senderManager, newestId,
                    out var senderNewestHistory);

                for (ulong t = 20; t <= 24; t++)
                {
                    senderGuaranteedHistory.Write(t, new TrackedInput((int)(100 + t)));
                    senderNewestHistory.Write(t, new TrackedInput((int)(300 + t)));
                }

                var receiverManager = CreateManager(receiverManagerObject);
                AddGuaranteedInputProbe(
                    receiverGuaranteedObject, receiverManager, guaranteedId, out _);
                AddNewestOnlyInputProbe(
                    receiverNewestObject, receiverManager, newestId, out _);

                SetLocalTick(senderManager, 24);
                using var frame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, frame, baselineTick: 20);
                int writtenBits = frame.positionInBits;

                frame.ResetPositionAndMode(true);
                ReadInputHistory(receiverManager, frame, serverTick: 24, baselineTick: 20);

                Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
            }
            finally
            {
                Object.DestroyImmediate(receiverNewestObject);
                Object.DestroyImmediate(receiverGuaranteedObject);
                Object.DestroyImmediate(senderNewestObject);
                Object.DestroyImmediate(senderGuaranteedObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void DecodingTheSameFrameTwiceYieldsIdenticalHistories()
        {
            var senderManagerObject = new GameObject("Input gap sender manager");
            var receiverManagerObject = new GameObject("Input gap receiver manager");
            var senderGuaranteedObject = new GameObject("Input gap sender guaranteed");
            var senderNewestObject = new GameObject("Input gap sender newest");
            var receiverGuaranteedObject = new GameObject("Input gap receiver guaranteed");
            var receiverNewestObject = new GameObject("Input gap receiver newest");
            try
            {
                var guaranteedId = new PredictedComponentID(new PredictedObjectID(620), 0);
                var newestId = new PredictedComponentID(new PredictedObjectID(621), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddGuaranteedInputProbe(
                    senderGuaranteedObject, senderManager, guaranteedId,
                    out var senderGuaranteedHistory);
                AddNewestOnlyInputProbe(
                    senderNewestObject, senderManager, newestId,
                    out var senderNewestHistory);

                for (ulong t = 12; t <= 16; t++)
                    senderGuaranteedHistory.Write(t, new TrackedInput((int)(100 + t)));
                senderNewestHistory.Write(16, new TrackedInput(777));

                var receiverManager = CreateManager(receiverManagerObject);
                AddGuaranteedInputProbe(
                    receiverGuaranteedObject, receiverManager, guaranteedId,
                    out var receiverGuaranteedHistory);
                AddNewestOnlyInputProbe(
                    receiverNewestObject, receiverManager, newestId,
                    out var receiverNewestHistory);

                SetLocalTick(senderManager, 16);
                using var frame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, frame, baselineTick: 12);
                int writtenBits = frame.positionInBits;

                for (var pass = 0; pass < 2; pass++)
                {
                    frame.ResetPositionAndMode(true);
                    ReadInputHistory(receiverManager, frame, serverTick: 16, baselineTick: 12);

                    Assert.That(frame.positionInBits, Is.EqualTo(writtenBits),
                        $"pass {pass} consumed a different bit count");
                    Assert.That(receiverGuaranteedHistory.Count, Is.EqualTo(4),
                        $"pass {pass} duplicated transcript entries");
                    for (ulong t = 13; t <= 16; t++)
                        AssertInput(receiverGuaranteedHistory, t, (int)(100 + t));
                    Assert.That(receiverNewestHistory.Count, Is.EqualTo(1),
                        $"pass {pass} duplicated newest entries");
                    AssertInput(receiverNewestHistory, 16, 777);
                }
            }
            finally
            {
                Object.DestroyImmediate(receiverNewestObject);
                Object.DestroyImmediate(receiverGuaranteedObject);
                Object.DestroyImmediate(senderNewestObject);
                Object.DestroyImmediate(senderGuaranteedObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void ZeroGuaranteedRosterWritesZeroTranscriptTicksRegardlessOfBaselineLag()
        {
            var senderManagerObject = new GameObject("Input zero-g sender manager");
            var receiverManagerObject = new GameObject("Input zero-g receiver manager");
            var senderNewestObject = new GameObject("Input zero-g sender newest");
            var receiverNewestObject = new GameObject("Input zero-g receiver newest");
            try
            {
                var newestId = new PredictedComponentID(new PredictedObjectID(630), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddNewestOnlyInputProbe(
                    senderNewestObject, senderManager, newestId,
                    out var senderNewestHistory);
                senderNewestHistory.Write(20, new TrackedInput(420));
                senderNewestHistory.Write(40, new TrackedInput(440));

                var receiverManager = CreateManager(receiverManagerObject);
                AddNewestOnlyInputProbe(
                    receiverNewestObject, receiverManager, newestId,
                    out var receiverNewestHistory);

                SetLocalTick(senderManager, 20);
                using (var smallLagFrame = BitPackerPool.Get())
                {
                    WriteVisibilityInputHistory(senderManager, smallLagFrame, baselineTick: 17);
                    smallLagFrame.ResetPositionAndMode(true);
                    Assert.That(ReadPackedUInt(smallLagFrame), Is.Zero);

                    smallLagFrame.ResetPositionAndMode(true);
                    ReadInputHistory(
                        receiverManager, smallLagFrame, serverTick: 20, baselineTick: 17);
                    AssertInput(receiverNewestHistory, 20, 420);
                }

                SetLocalTick(senderManager, 40);
                using var largeLagFrame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, largeLagFrame, baselineTick: 2);
                largeLagFrame.ResetPositionAndMode(true);
                Assert.That(ReadPackedUInt(largeLagFrame), Is.Zero,
                    "a lag beyond the window must not resurrect the transcript for a roster without guaranteed systems");

                largeLagFrame.ResetPositionAndMode(true);
                ReadInputHistory(receiverManager, largeLagFrame, serverTick: 40, baselineTick: 2);
                AssertInput(receiverNewestHistory, 40, 440);
            }
            finally
            {
                Object.DestroyImmediate(receiverNewestObject);
                Object.DestroyImmediate(senderNewestObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void TranscriptWindowClampsToMaxInputWindowTicks()
        {
            var senderManagerObject = new GameObject("Input clamp sender manager");
            var receiverManagerObject = new GameObject("Input clamp receiver manager");
            var senderGuaranteedObject = new GameObject("Input clamp sender guaranteed");
            var receiverGuaranteedObject = new GameObject("Input clamp receiver guaranteed");
            try
            {
                var maxWindow = MaxInputWindow();
                Assert.That(maxWindow, Is.EqualTo(32));

                var guaranteedId = new PredictedComponentID(new PredictedObjectID(640), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddGuaranteedInputProbe(
                    senderGuaranteedObject, senderManager, guaranteedId,
                    out var senderGuaranteedHistory);
                for (ulong t = 15; t <= 50; t++)
                    senderGuaranteedHistory.Write(t, new TrackedInput((int)(100 + t)));

                var receiverManager = CreateManager(receiverManagerObject);
                AddGuaranteedInputProbe(
                    receiverGuaranteedObject, receiverManager, guaranteedId,
                    out var receiverGuaranteedHistory);

                SetLocalTick(senderManager, 50);
                using var frame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, frame, baselineTick: 10);
                int writtenBits = frame.positionInBits;

                frame.ResetPositionAndMode(true);
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(maxWindow));

                frame.ResetPositionAndMode(true);
                ReadInputHistory(receiverManager, frame, serverTick: 50, baselineTick: 10);

                Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));
                for (ulong t = 51 - maxWindow; t <= 50; t++)
                    AssertInput(receiverGuaranteedHistory, t, (int)(100 + t));
                Assert.That(receiverGuaranteedHistory.TryGet(50 - maxWindow, out _), Is.False,
                    "ticks older than the clamped window must not be delivered");
                Assert.That(receiverGuaranteedHistory.TryGet(15, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(receiverGuaranteedObject);
                Object.DestroyImmediate(senderGuaranteedObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void BaselineTickZeroWritesTheNewestBlockPayloadInFull()
        {
            var senderManagerObject = new GameObject("Input zero-base sender manager");
            var receiverManagerObject = new GameObject("Input zero-base receiver manager");
            var senderNewestObject = new GameObject("Input zero-base sender newest");
            var receiverNewestObject = new GameObject("Input zero-base receiver newest");
            try
            {
                var newestId = new PredictedComponentID(new PredictedObjectID(650), 0);

                var senderManager = CreateManager(senderManagerObject);
                AddNewestOnlyInputProbe(
                    senderNewestObject, senderManager, newestId,
                    out var senderNewestHistory);
                senderNewestHistory.Write(16, new TrackedInput(555));
                senderNewestHistory.Write(20, new TrackedInput(555));

                var receiverManager = CreateManager(receiverManagerObject);
                AddNewestOnlyInputProbe(
                    receiverNewestObject, receiverManager, newestId,
                    out var receiverNewestHistory);

                SetLocalTick(senderManager, 16);
                using (var primingFrame = BitPackerPool.Get())
                {
                    WriteVisibilityInputHistory(senderManager, primingFrame, baselineTick: 0);
                    primingFrame.ResetPositionAndMode(true);
                    ReadInputHistory(
                        receiverManager, primingFrame, serverTick: 16, baselineTick: 0);
                    AssertInput(receiverNewestHistory, 16, 555);
                }

                SetLocalTick(senderManager, 20);
                using var frame = BitPackerPool.Get();
                WriteVisibilityInputHistory(senderManager, frame, baselineTick: 0);
                int writtenBits = frame.positionInBits;

                frame.ResetPositionAndMode(true);
                Assert.That(ReadPackedUInt(frame), Is.Zero);
                Assert.That(ReadPackedUInt(frame), Is.EqualTo(1));
                PredictedComponentID pid = default;
                Packer<PredictedComponentID>.Read(frame, ref pid);
                Assert.That(pid, Is.EqualTo(newestId));
                Assert.That(Packer<bool>.Read(frame), Is.False,
                    "baseline tick 0 must force a full payload even when a bitwise-equal cached block exists");
                var payloadBits = ReadPackedUInt(frame);
                int payloadStart = frame.positionInBits;
                Assert.That(Packer<bool>.Read(frame), Is.True);
                Assert.That(Packer<TrackedInput>.Read(frame).id, Is.EqualTo(555));
                Assert.That(frame.positionInBits - payloadStart, Is.EqualTo((int)payloadBits));
                Assert.That(frame.positionInBits, Is.EqualTo(writtenBits));

                frame.ResetPositionAndMode(true);
                ReadInputHistory(receiverManager, frame, serverTick: 20, baselineTick: 0);
                AssertInput(receiverNewestHistory, 20, 555);
            }
            finally
            {
                Object.DestroyImmediate(receiverNewestObject);
                Object.DestroyImmediate(senderNewestObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        private static PredictionManager CreateManager(GameObject managerObject)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(
                typeof(PredictionManager),
                manager,
                "<tickRate>k__BackingField",
                20);
            return manager;
        }

        private static DeterministicInputProbe AddGuaranteedInputProbe(
            GameObject gameObject,
            PredictionManager manager,
            PredictedComponentID id,
            out History<TrackedInput> inputHistory)
        {
            var probe = gameObject.AddComponent<DeterministicInputProbe>();
            probe.id = id;
            SetField(
                typeof(PredictedIdentity),
                probe,
                "<predictionManager>k__BackingField",
                manager);
            inputHistory = new History<TrackedInput>(200);
            SetField(
                typeof(DeterministicIdentity<TrackedInput, EmptyState>),
                probe,
                "_inputHistory",
                inputHistory);
            RegisterSystem(manager, probe);
            return probe;
        }

        private static StatefulInputProbe AddNewestOnlyInputProbe(
            GameObject gameObject,
            PredictionManager manager,
            PredictedComponentID id,
            out History<TrackedInput> inputHistory)
        {
            var probe = gameObject.AddComponent<StatefulInputProbe>();
            probe.id = id;
            SetField(
                typeof(PredictedIdentity),
                probe,
                "<predictionManager>k__BackingField",
                manager);
            inputHistory = new History<TrackedInput>(200);
            SetField(
                typeof(PredictedIdentity<TrackedInput, EmptyState>),
                probe,
                "_inputHistory",
                inputHistory);
            RegisterSystem(manager, probe);
            return probe;
        }

        private static void RegisterSystem(
            PredictionManager manager,
            PredictedIdentity identity)
        {
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
            var instanceMap =
                GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager),
                    manager,
                    "_instanceMap");
            instanceMap[identity.id] = identity;
            if (identity.requiresGuaranteedInputHistory)
            {
                var guaranteedCount = GetField<int>(
                    typeof(PredictionManager),
                    manager,
                    "_guaranteedInputHistorySystems");
                SetField(
                    typeof(PredictionManager),
                    manager,
                    "_guaranteedInputHistorySystems",
                    guaranteedCount + 1);
            }
        }

        private static void WriteVisibilityInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong baselineTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteVisibilityInputHistory",
                InstanceFields);
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

        private static void ReadInputHistory(
            PredictionManager manager,
            BitPacker frame,
            ulong serverTick,
            ulong baselineTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ReadInputHistory",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(manager, new object[] { frame, serverTick, baselineTick });
        }

        private static ulong MaxInputWindow()
        {
            var field = typeof(PredictionManager).GetField(
                "MaxInputWindow",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null,
                "Missing const PredictionManager.MaxInputWindow");
            return (ulong)field.GetValue(null);
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static uint ReadPackedUInt(BitPacker frame)
        {
            PackedUInt value = default;
            Packer<PackedUInt>.Read(frame, ref value);
            return value.value;
        }

        private static void AssertInput(
            History<TrackedInput> history,
            ulong tick,
            int expected)
        {
            Assert.That(history.TryGet(tick, out var value), Is.True,
                $"missing decoded input at tick {tick}");
            Assert.That(value.id, Is.EqualTo(expected),
                $"wrong decoded input value at tick {tick}");
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
