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
    public sealed class NewestInputRingProtocolTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const ulong StartTick = 2;
        private const ulong PhaseOneEndTick = 91;
        private const ulong BlackoutTicks = 40;
        private const ulong MaxInputWindow = 32;

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
        public void NewestBlockRepeatProtocolSurvivesLossReorderingAndAckStall()
        {
            var serverManagerObject = new GameObject("Newest ring server manager");
            var clientManagerObject = new GameObject("Newest ring client manager");
            var freshManagerObject = new GameObject("Newest ring fresh manager");
            var serverProbeObject = new GameObject("Newest ring server probe");
            var clientProbeObject = new GameObject("Newest ring client probe");
            var freshProbeObject = new GameObject("Newest ring fresh probe");
            var allFrames = new List<WrittenFrame>();
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(600), 0);

                var serverManager = CreateManager(serverManagerObject);
                var serverProbe = serverProbeObject.AddComponent<StatefulInputProbe>();
                AttachIdentity(serverProbe, serverManager, id);
                var serverHistory = new History<TrackedInput>(256);
                SetField(
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    serverProbe,
                    "_inputHistory",
                    serverHistory);
                RegisterSystem(serverManager, serverProbe);

                var clientManager = CreateManager(clientManagerObject);
                var clientProbe = clientProbeObject.AddComponent<StatefulInputProbe>();
                AttachIdentity(clientProbe, clientManager, id);
                var clientHistory = new History<TrackedInput>(256);
                SetField(
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    clientProbe,
                    "_inputHistory",
                    clientHistory);
                RegisterSystem(clientManager, clientProbe);

                var writeMethod = typeof(PredictionManager).GetMethod(
                    "WriteVisibilityInputHistory",
                    InstanceFields);
                Assert.That(writeMethod, Is.Not.Null);
                var readMethod = typeof(PredictionManager).GetMethod(
                    "ReadInputHistory",
                    InstanceFields);
                Assert.That(readMethod, Is.Not.Null);

                var timeline = new PlayerVisibilityTimeline();
                var seeded = new Dictionary<ulong, int>();
                var pending = new List<PendingDelivery>();
                var due = new List<PendingDelivery>();
                ulong rngState = 0x9E3779B97F4A7C15UL;
                ulong lastApplied = 0;
                int sequence = 0;
                int droppedFrames = 0;
                int appliedFrames = 0;
                int gatedDeliveries = 0;
                int appliedRepeatFrames = 0;

                for (ulong tick = StartTick; tick <= PhaseOneEndTick; tick++)
                {
                    int value = 100 + (int)(tick / 5);
                    seeded[tick] = value;
                    serverHistory.Write(tick, new TrackedInput(value));

                    var frame = WriteFrame(
                        serverManager, writeMethod, timeline, tick, lastApplied);
                    allFrames.Add(frame);
                    ParseNewestBlock(frame, id);

                    uint dropRoll = NextRandom(ref rngState) % 100;
                    uint delay = NextRandom(ref rngState) % 3;
                    uint duplicateRoll = NextRandom(ref rngState) % 100;
                    uint duplicateExtraDelay = NextRandom(ref rngState) % 2;

                    if (dropRoll < 30)
                    {
                        droppedFrames++;
                    }
                    else
                    {
                        pending.Add(new PendingDelivery
                        {
                            deliverAtTick = tick + delay,
                            sequence = sequence++,
                            frame = frame
                        });
                        if (duplicateRoll < 15)
                        {
                            pending.Add(new PendingDelivery
                            {
                                deliverAtTick = tick + delay + 1 + duplicateExtraDelay,
                                sequence = sequence++,
                                frame = frame
                            });
                        }
                    }

                    due.Clear();
                    for (var i = pending.Count - 1; i >= 0; i--)
                    {
                        if (pending[i].deliverAtTick <= tick)
                        {
                            due.Add(pending[i]);
                            pending.RemoveAt(i);
                        }
                    }
                    due.Sort(static (a, b) =>
                        a.deliverAtTick != b.deliverAtTick
                            ? a.deliverAtTick.CompareTo(b.deliverAtTick)
                            : a.sequence.CompareTo(b.sequence));

                    for (var i = 0; i < due.Count; i++)
                    {
                        var delivered = due[i].frame;
                        if (delivered.serverTick <= lastApplied)
                        {
                            gatedDeliveries++;
                            continue;
                        }

                        ApplyFrame(clientManager, readMethod, delivered);
                        lastApplied = delivered.serverTick;
                        appliedFrames++;
                        if (delivered.repeatEntries > 0)
                            appliedRepeatFrames++;

                        Assert.That(
                            clientHistory.TryGet(delivered.serverTick, out var received),
                            Is.True,
                            $"tick {delivered.serverTick}: applied frame left no input sample");
                        Assert.That(
                            received.id,
                            Is.EqualTo(seeded[delivered.serverTick]),
                            $"tick {delivered.serverTick}: decoded input diverged from the seed");
                    }
                }

                Assert.That(droppedFrames, Is.GreaterThanOrEqualTo(20),
                    "the schedule must exercise frame loss");
                Assert.That(appliedFrames, Is.GreaterThanOrEqualTo(45));
                Assert.That(gatedDeliveries, Is.GreaterThanOrEqualTo(8),
                    "duplicates and reordering must exercise the stale-frame gate");
                Assert.That(appliedRepeatFrames, Is.GreaterThanOrEqualTo(20),
                    "the schedule must exercise the repeat-reference decode path");
                Assert.That(lastApplied, Is.GreaterThan(0UL));

                ulong frozenBaseline = lastApplied;
                int frozenValue = seeded[frozenBaseline];
                WrittenFrame firstStalledRepeatFrame = null;
                WrittenFrame lastCappedFrame = null;
                int stalledRepeatFrames = 0;
                int cappedFrames = 0;

                for (ulong tick = PhaseOneEndTick + 1;
                     tick <= PhaseOneEndTick + BlackoutTicks;
                     tick++)
                {
                    seeded[tick] = frozenValue;
                    serverHistory.Write(tick, new TrackedInput(frozenValue));

                    var frame = WriteFrame(
                        serverManager, writeMethod, timeline, tick, frozenBaseline);
                    allFrames.Add(frame);
                    ParseNewestBlock(frame, id);

                    ulong ackLag = tick - frozenBaseline;
                    if (ackLag <= MaxInputWindow)
                    {
                        Assert.That(frame.repeatEntries, Is.EqualTo(1),
                            $"tick {tick}: an in-window unchanged input must ride the repeat bit");
                        Assert.That(frame.payloadEntries, Is.Zero, $"tick {tick}");
                        stalledRepeatFrames++;
                        firstStalledRepeatFrame ??= frame;
                    }
                    else
                    {
                        Assert.That(frame.repeatEntries, Is.Zero,
                            $"tick {tick}: past the {MaxInputWindow}-tick block cache the writer " +
                            "must stop emitting repeats");
                        Assert.That(frame.payloadEntries, Is.EqualTo(1), $"tick {tick}");
                        cappedFrames++;
                        lastCappedFrame = frame;
                    }
                }

                Assert.That(stalledRepeatFrames, Is.GreaterThan(0));
                Assert.That(cappedFrames, Is.GreaterThan(0));

                var freshManager = CreateManager(freshManagerObject);
                var freshProbe = freshProbeObject.AddComponent<StatefulInputProbe>();
                AttachIdentity(freshProbe, freshManager, id);
                var freshHistory = new History<TrackedInput>(256);
                SetField(
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    freshProbe,
                    "_inputHistory",
                    freshHistory);
                RegisterSystem(freshManager, freshProbe);

                ApplyFrame(freshManager, readMethod, lastCappedFrame);
                Assert.That(
                    freshHistory.TryGet(lastCappedFrame.serverTick, out var recovered),
                    Is.True,
                    "a receiver with an empty newest-input ring must decode a capped frame");
                Assert.That(recovered.id, Is.EqualTo(frozenValue));

                firstStalledRepeatFrame.bits.ResetPositionAndMode(true);
                var control = Assert.Throws<TargetInvocationException>(() =>
                    readMethod.Invoke(freshManager, new object[]
                    {
                        firstStalledRepeatFrame.bits,
                        firstStalledRepeatFrame.serverTick,
                        firstStalledRepeatFrame.baselineTick
                    }));
                Assert.That(control.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(control.InnerException.Message, Does.Contain("repeats a payload"));
            }
            finally
            {
                for (var i = 0; i < allFrames.Count; i++)
                    allFrames[i].bits.Dispose();
                Object.DestroyImmediate(freshProbeObject);
                Object.DestroyImmediate(clientProbeObject);
                Object.DestroyImmediate(serverProbeObject);
                Object.DestroyImmediate(freshManagerObject);
                Object.DestroyImmediate(clientManagerObject);
                Object.DestroyImmediate(serverManagerObject);
            }
        }

        private sealed class WrittenFrame
        {
            public ulong serverTick;
            public ulong baselineTick;
            public BitPacker bits;
            public int repeatEntries;
            public int payloadEntries;
        }

        private struct PendingDelivery
        {
            public ulong deliverAtTick;
            public int sequence;
            public WrittenFrame frame;
        }

        private static uint NextRandom(ref ulong state)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (uint)(state >> 33);
        }

        private static WrittenFrame WriteFrame(
            PredictionManager server,
            MethodInfo writeMethod,
            PlayerVisibilityTimeline timeline,
            ulong serverTick,
            ulong baselineTick)
        {
            SetLocalTick(server, serverTick);
            var frame = new WrittenFrame
            {
                serverTick = serverTick,
                baselineTick = baselineTick,
                bits = BitPackerPool.Get()
            };
            writeMethod.Invoke(
                server,
                new object[] { default(PlayerID), frame.bits, baselineTick, timeline });
            return frame;
        }

        private static void ParseNewestBlock(
            WrittenFrame frame,
            PredictedComponentID expectedId)
        {
            frame.bits.ResetPositionAndMode(true);
            PackedUInt transcriptTicks = default;
            Packer<PackedUInt>.Read(frame.bits, ref transcriptTicks);
            Assert.That(transcriptTicks.value, Is.Zero,
                "a roster without guaranteed-history systems must write an empty transcript");

            PackedUInt entryCount = default;
            Packer<PackedUInt>.Read(frame.bits, ref entryCount);
            Assert.That(entryCount.value, Is.EqualTo(1),
                $"tick {frame.serverTick}: the seeded probe must appear in the newest block");

            for (uint e = 0; e < entryCount.value; e++)
            {
                PredictedComponentID pid = default;
                Packer<PredictedComponentID>.Read(frame.bits, ref pid);
                Assert.That(pid, Is.EqualTo(expectedId));

                if (Packer<bool>.Read(frame.bits))
                {
                    frame.repeatEntries++;
                }
                else
                {
                    PackedUInt payloadBits = default;
                    Packer<PackedUInt>.Read(frame.bits, ref payloadBits);
                    frame.bits.SkipBits(checked((int)payloadBits.value));
                    frame.payloadEntries++;
                }
            }
        }

        private static void ApplyFrame(
            PredictionManager client,
            MethodInfo readMethod,
            WrittenFrame frame)
        {
            frame.bits.ResetPositionAndMode(true);
            try
            {
                readMethod.Invoke(
                    client,
                    new object[] { frame.bits, frame.serverTick, frame.baselineTick });
            }
            catch (TargetInvocationException e)
            {
                Assert.Fail(
                    $"Frame {frame.serverTick} (baseline {frame.baselineTick}) " +
                    $"failed to decode: {e.InnerException}");
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

        private static void AttachIdentity(
            PredictedIdentity identity,
            PredictionManager manager,
            PredictedComponentID id)
        {
            identity.id = id;
            SetField(
                typeof(PredictedIdentity),
                identity,
                "<predictionManager>k__BackingField",
                manager);
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
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
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
