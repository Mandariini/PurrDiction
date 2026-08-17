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
    public sealed class UploadRoundTripTests
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
        public void ConstantWindowUsesRepeatBitsAndStillExpandsEveryTick()
        {
            var clientObject = new GameObject("Constant upload client");
            var serverObject = new GameObject("Constant upload server");
            var probeObject = new GameObject("Constant upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(700), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    _ => 7);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL),
                    "without guaranteed systems the window must start at the redundancy cap");
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                Assert.That(parsed[0].entries.Count, Is.EqualTo(1));
                Assert.That(parsed[0].entries[0].id, Is.EqualTo(probeId));
                Assert.That(parsed[0].entries[0].repeat, Is.False);
                for (var i = 1; i < 5; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(1));
                    Assert.That(parsed[i].entries[0].id, Is.EqualTo(probeId));
                    Assert.That(parsed[i].entries[0].repeat, Is.True,
                        "an unchanged input payload must ride the repeat bit");
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                Assert.That(queue.highestReceivedTick, Is.EqualTo(40UL));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].hasInput, Is.True);
                    Assert.That(decoded[0].value, Is.EqualTo(7),
                        $"tick {tick} did not expand the repeated payload");
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void ChangingInputsWriteFullPayloadsForEveryTick()
        {
            var clientObject = new GameObject("Changing upload client");
            var serverObject = new GameObject("Changing upload server");
            var probeObject = new GameObject("Changing upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(710), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 100 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(36UL));
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < 5; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(1));
                    Assert.That(parsed[i].entries[0].repeat, Is.False);
                    Assert.That(parsed[i].entries[0].payloadBitLength, Is.GreaterThan(0));
                }

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].hasInput, Is.True);
                    Assert.That(decoded[0].value, Is.EqualTo(100 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void GuaranteedTranscriptExtendsBelowTheCapAndReentersWithFullPayload()
        {
            var clientObject = new GameObject("Mixed upload client");
            var serverObject = new GameObject("Mixed upload server");
            var deterministicObject = new GameObject("Mixed upload deterministic probe");
            var statefulObject = new GameObject("Mixed upload stateful probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 20);

                var deterministic =
                    deterministicObject.AddComponent<DeterministicInputProbe>();
                var deterministicId =
                    new PredictedComponentID(new PredictedObjectID(720), 0);
                AttachIdentity(deterministic, client, deterministicId);
                SeedInputs(
                    deterministic,
                    typeof(DeterministicIdentity<TrackedInput, EmptyState>),
                    21,
                    40,
                    tick => 1000 + (int)tick);

                var stateful = statefulObject.AddComponent<StatefulInputProbe>();
                var statefulId = new PredictedComponentID(new PredictedObjectID(721), 0);
                AttachIdentity(stateful, client, statefulId);
                SeedInputs(
                    stateful,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    21,
                    40,
                    _ => 5);

                FinalizeInput(client, deterministic, stateful);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(firstTick, Is.EqualTo(21UL),
                    "a guaranteed-history system must keep the window at the ack frontier");
                Assert.That(tickCount, Is.EqualTo(20U));

                var parsed = ParseUpload(cached, tickCount);
                for (var i = 0; i < 15; i++)
                {
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(1),
                        $"tick {21 + i} below the cap must carry only the guaranteed system");
                    Assert.That(parsed[i].entries[0].id, Is.EqualTo(deterministicId));
                    Assert.That(parsed[i].entries[0].repeat, Is.False);
                }

                for (var i = 15; i < 20; i++)
                    Assert.That(parsed[i].entries.Count, Is.EqualTo(2));

                // The stateful payload is constant across the window, so only its absence
                // from the tick-35 block can force the full payload here.
                Assert.That(FindEntry(parsed[15], statefulId).repeat, Is.False,
                    "a system absent from the previous tick must never repeat into it");
                for (var i = 16; i < 20; i++)
                    Assert.That(FindEntry(parsed[i], statefulId).repeat, Is.True);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(20));
                for (ulong tick = 21; tick <= 35; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(deterministicId));
                    Assert.That(decoded[0].value, Is.EqualTo(1000 + (int)tick));
                }

                for (ulong tick = 36; tick <= 40; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(2));
                    Assert.That(decoded[0].id, Is.EqualTo(deterministicId));
                    Assert.That(decoded[0].value, Is.EqualTo(1000 + (int)tick));
                    Assert.That(decoded[1].id, Is.EqualTo(statefulId));
                    Assert.That(decoded[1].hasInput, Is.True);
                    Assert.That(decoded[1].value, Is.EqualTo(5));
                }
            }
            finally
            {
                Object.DestroyImmediate(statefulObject);
                Object.DestroyImmediate(deterministicObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void RedeliveredUploadLeavesTheServerQueueUntouched()
        {
            var clientObject = new GameObject("Hedge upload client");
            var serverObject = new GameObject("Hedge upload server");
            var probeObject = new GameObject("Hedge upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(730), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 200 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(5));
                var snapshot = new Dictionary<ulong, BitPacker>();
                foreach (var pair in queue.byTick)
                    snapshot[pair.Key] = pair.Value.inputPacket;
                var rawHighest = queue.rawHighestReceivedTick;
                var highest = queue.highestReceivedTick;

                Deliver(server, firstTick, tickCount, CopyForRead(cached), sender);

                Assert.That(queue.byTick.Count, Is.EqualTo(5),
                    "a fully duplicate hedge resend must not grow the queue");
                Assert.That(queue.rawHighestReceivedTick, Is.EqualTo(rawHighest));
                Assert.That(queue.highestReceivedTick, Is.EqualTo(highest));
                for (ulong tick = 36; tick <= 40; tick++)
                {
                    Assert.That(queue.byTick[tick].inputPacket, Is.SameAs(snapshot[tick]),
                        $"tick {tick} slice was rebuilt by a duplicate packet");
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].value, Is.EqualTo(200 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        [Test]
        public void CorruptedMiddleTickAbortsExpansionAndKeepsEarlierTicks()
        {
            var clientObject = new GameObject("Corrupt upload client");
            var serverObject = new GameObject("Corrupt upload server");
            var probeObject = new GameObject("Corrupt upload probe");
            try
            {
                var client = CreateManager(clientObject);
                SetLocalTick(client, 40);
                SetInputAckTick(client, 30);

                var probe = probeObject.AddComponent<StatefulInputProbe>();
                var probeId = new PredictedComponentID(new PredictedObjectID(740), 0);
                AttachIdentity(probe, client, probeId);
                SeedInputs(
                    probe,
                    typeof(PredictedIdentity<TrackedInput, EmptyState>),
                    30,
                    40,
                    tick => 300 + (int)tick);

                FinalizeInput(client, probe);
                var cached = GetCachedPayload(client, out var firstTick, out var tickCount);
                Assert.That(tickCount, Is.EqualTo(5U));

                var parsed = ParseUpload(cached, tickCount);
                Assert.That(parsed[2].entries[0].repeat, Is.False);

                // Flipping the tick-38 repeat flag makes the parser resolve the entry from
                // the previous tick and skip the payload that is still on the wire, so the
                // consumed size no longer matches the declared blockBits and the packet
                // must be abandoned from that tick onward.
                var corrupted = BitPackerPool.Get();
                corrupted.WriteBitsWithoutConsumingIt(cached, cached.positionInBits);
                corrupted.WriteAt(parsed[2].entries[0].repeatBitPosition, true);
                corrupted.ResetPositionAndMode(true);

                var server = CreateManager(serverObject);
                SetLocalTick(server, 2);
                var sender = new PlayerID(9, false);
                Deliver(server, firstTick, tickCount, corrupted, sender);

                var queue = GetQueue(server, sender);
                Assert.That(queue.byTick.Count, Is.EqualTo(2));
                Assert.That(queue.byTick.ContainsKey(36), Is.True);
                Assert.That(queue.byTick.ContainsKey(37), Is.True);
                Assert.That(queue.byTick.ContainsKey(38), Is.False,
                    "the corrupt tick must not be queued");
                Assert.That(queue.byTick.ContainsKey(39), Is.False,
                    "ticks after the corrupt one must not be queued");
                Assert.That(queue.byTick.ContainsKey(40), Is.False,
                    "ticks after the corrupt one must not be queued");

                for (ulong tick = 36; tick <= 37; tick++)
                {
                    var decoded = DecodeSlice(queue.byTick[tick]);
                    Assert.That(decoded.Count, Is.EqualTo(1));
                    Assert.That(decoded[0].id, Is.EqualTo(probeId));
                    Assert.That(decoded[0].value, Is.EqualTo(300 + (int)tick));
                }
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(serverObject);
                Object.DestroyImmediate(clientObject);
            }
        }

        private sealed class ParsedUploadEntry
        {
            public PredictedComponentID id;
            public bool repeat;
            public int repeatBitPosition;
            public int payloadBitLength;
        }

        private sealed class ParsedUploadTick
        {
            public uint declaredBlockBits;
            public readonly List<ParsedUploadEntry> entries = new List<ParsedUploadEntry>();
        }

        private readonly struct DecodedInputEntry
        {
            public readonly PredictedComponentID id;
            public readonly bool hasInput;
            public readonly int value;

            public DecodedInputEntry(PredictedComponentID id, bool hasInput, int value)
            {
                this.id = id;
                this.hasInput = hasInput;
                this.value = value;
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

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static void SetInputAckTick(PredictionManager manager, ulong tick)
        {
            SetField(typeof(PredictionManager), manager, "_inputAckTick", tick);
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

        private static void SeedInputs(
            Component identity,
            Type declaringType,
            ulong fromTick,
            ulong toTick,
            Func<ulong, int> value)
        {
            var history = new History<TrackedInput>(200);
            for (var tick = fromTick; tick <= toTick; tick++)
                history.Write(tick, new TrackedInput(value(tick)));
            SetField(declaringType, identity, "_inputHistory", history);
        }

        private static void FinalizeInput(
            PredictionManager client,
            params PredictedIdentity[] owned)
        {
            var method = typeof(PredictionManager).GetMethod(
                "FinalizeInputOnClient",
                InstanceFields);
            Assert.That(method, Is.Not.Null);

            var list = DisposableList<PredictedIdentity>.Create(owned.Length);
            try
            {
                for (var i = 0; i < owned.Length; i++)
                    list.Add(owned[i]);

                // The RPC send at the end of FinalizeInputOnClient is a no-op for an
                // unspawned identity on an unreliable channel; the payload cache is
                // written before the send either way.
                try
                {
                    method.Invoke(client, new object[] { list });
                }
                catch (TargetInvocationException)
                {
                }
            }
            finally
            {
                list.Dispose();
            }
        }

        private static BitPacker GetCachedPayload(
            PredictionManager client,
            out ulong firstTick,
            out uint tickCount)
        {
            var payload = GetField<BitPacker>(
                typeof(PredictionManager),
                client,
                "_cachedInputPayload");
            Assert.That(payload, Is.Not.Null,
                "FinalizeInputOnClient must cache the payload before sending");
            firstTick = GetField<ulong>(
                typeof(PredictionManager),
                client,
                "_cachedInputFirstTick");
            tickCount = GetField<uint>(
                typeof(PredictionManager),
                client,
                "_cachedInputTickCount");
            return payload;
        }

        private static BitPacker CopyForRead(BitPacker source)
        {
            var copy = BitPackerPool.Get();
            copy.WriteBitsWithoutConsumingIt(source, source.positionInBits);
            copy.ResetPositionAndMode(true);
            return copy;
        }

        private static void Deliver(
            PredictionManager server,
            ulong firstTick,
            uint tickCount,
            BitPacker payload,
            PlayerID sender)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ReceivedInput",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            var info = new RPCInfo { sender = sender };
            method.Invoke(server, new object[] { firstTick, tickCount, 0UL, payload, info });
        }

        private static PredictionManager.InputQueue GetQueue(
            PredictionManager server,
            PlayerID sender)
        {
            var clientTicks =
                GetField<Dictionary<PlayerID, PredictionManager.InputQueue>>(
                    typeof(PredictionManager),
                    server,
                    "_clientTicks");
            Assert.That(clientTicks.TryGetValue(sender, out var queue), Is.True,
                "the server never registered the sender's input queue");
            return queue;
        }

        private static List<ParsedUploadTick> ParseUpload(
            BitPacker cachedPayload,
            uint tickCount)
        {
            var payload = CopyForRead(cachedPayload);
            try
            {
                var result = new List<ParsedUploadTick>();
                for (uint i = 0; i < tickCount; i++)
                {
                    PackedUInt blockBits = default;
                    Packer<PackedUInt>.Read(payload, ref blockBits);
                    PackedUInt entryCount = default;
                    Packer<PackedUInt>.Read(payload, ref entryCount);
                    int blockStart = payload.positionInBits;

                    var tickBlock = new ParsedUploadTick
                    {
                        declaredBlockBits = blockBits.value
                    };

                    for (uint e = 0; e < entryCount.value; e++)
                    {
                        PredictedComponentID pid = default;
                        Packer<PredictedComponentID>.Read(payload, ref pid);
                        int repeatBitPosition = payload.positionInBits;
                        bool repeat = Packer<bool>.Read(payload);
                        int payloadBitLength = 0;
                        if (!repeat)
                        {
                            PackedUInt bits = default;
                            Packer<PackedUInt>.Read(payload, ref bits);
                            payloadBitLength = checked((int)bits.value);
                            payload.SkipBits(payloadBitLength);
                        }

                        tickBlock.entries.Add(new ParsedUploadEntry
                        {
                            id = pid,
                            repeat = repeat,
                            repeatBitPosition = repeatBitPosition,
                            payloadBitLength = payloadBitLength
                        });
                    }

                    Assert.That(
                        payload.positionInBits - blockStart,
                        Is.EqualTo((int)blockBits.value),
                        $"tick block {i} declared size does not match its contents");
                    result.Add(tickBlock);
                }

                return result;
            }
            finally
            {
                payload.Dispose();
            }
        }

        private static ParsedUploadEntry FindEntry(
            ParsedUploadTick tickBlock,
            PredictedComponentID id)
        {
            for (var i = 0; i < tickBlock.entries.Count; i++)
            {
                if (tickBlock.entries[i].id.Equals(id))
                    return tickBlock.entries[i];
            }

            Assert.Fail($"No entry for {id} in the tick block");
            return null;
        }

        private static List<DecodedInputEntry> DecodeSlice(
            PredictionManager.InputQueueValue entry)
        {
            var packet = entry.inputPacket;
            var result = new List<DecodedInputEntry>();
            for (uint i = 0; i < entry.count.value; i++)
            {
                PredictedComponentID pid = default;
                Packer<PredictedComponentID>.Read(packet, ref pid);
                bool hasInput = Packer<bool>.Read(packet);
                int value = 0;
                if (hasInput)
                    value = Packer<TrackedInput>.Read(packet).id;
                result.Add(new DecodedInputEntry(pid, hasInput, value));
            }

            return result;
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
