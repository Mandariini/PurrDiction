using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Real frame writers, codecs, receive queue, verified replay and input ACK handler.
    // Transport handoff is represented by an owned packet copy and BeginFullFrame;
    // these tests do not invoke generated RPC senders or claim socket coverage.
    public sealed class CheckpointFrameDeliveryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(CheckpointProbeState));
            Packer<CheckpointProbeState>.RegisterWriter((packer, state) => Packer<int>.Write(packer, state.value));
            Packer<CheckpointProbeState>.RegisterReader((BitPacker packer, ref CheckpointProbeState state) =>
                state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void ContinuationIsOpaqueUntilItsFullThenLateFramesCannotReplaceStateMetadataOrInput()
        {
            using var f = new Fixture();
            using var old = f.Prepare(10, 100);
            using var full = f.Prepare(11, 110, full: true);
            var owner = new PlayerID(new PackedULong(7), false);
            using var continuation = f.Prepare(12, 120, owner: owner);
            Assert.That(continuation.baseline, Is.EqualTo(11));
            Assert.That(continuation.checkpoint, Is.EqualTo(11));
            Assert.That(f.ServerAck, Is.EqualTo(8), "the full has not been acknowledged");

            f.Receive(continuation);
            f.Drain();
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.ClientVerified, Is.EqualTo(10));
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(8));
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.probe.inputReads, Is.Zero,
                "staging must not decode the authoritative input transcript");

            f.Receive(full);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.ClientVerified, Is.EqualTo(12));
            Assert.That(f.Verified(12).state.value, Is.EqualTo(120));
            Assert.That(f.Verified(12).prediction.owner, Is.EqualTo(owner));
            Assert.That(f.probe.lastInput, Is.EqualTo(120));
            Assert.That(f.probe.deltaReads, Is.EqualTo(1));
            int inputs = f.probe.inputReads;

            f.Receive(old);
            f.Receive(full); // delayed duplicate full after its continuation was applied
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.probe.deltaReads, Is.EqualTo(1));
            Assert.That(f.probe.inputReads, Is.EqualTo(inputs));
            Assert.That(f.Verified(12).prediction.owner, Is.EqualTo(owner));
            Assert.That(f.probe.lastInput, Is.EqualTo(120));
        }

        [Test]
        public void StagedContinuationAgeCountsFromItsReleaseNotItsArrival()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            using var continuation = f.Prepare(12, 120);
            Set(f.client, "<localTick>k__BackingField", 14UL);
            Set(f.client, "<localTickInContext>k__BackingField", 14UL);

            try
            {
                PredictionManager.frameCountOverrideForTests = 1000;
                f.Receive(continuation);
                f.Drain();
                Assert.That(f.client.stagedCheckpointFrames, Is.EqualTo(1), "the delta waits for its checkpoint");

                // The checkpoint lands many render frames later, as it does behind packet loss.
                PredictionManager.frameCountOverrideForTests = 1000 + 1687;
                f.Receive(full);
                f.Drain();
            }
            finally
            {
                PredictionManager.frameCountOverrideForTests = null;
            }

            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(12));
            Assert.That(f.client.maxFrameApplyAgeFrames, Is.Zero,
                "waiting for a checkpoint is delivery, not apply latency; both frames applied the frame they became eligible");
        }

        [Test]
        public void NewestStagedContinuationWinsRegardlessOfArrivalOrder()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            using var older = f.Prepare(12, 120);
            using var newer = f.Prepare(13, 130);
            f.Receive(newer);
            f.Receive(older);
            f.Receive(newer);
            f.Drain();
            Assert.That(f.probe.inputReads, Is.Zero);
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.ClientAck, Is.EqualTo(10));

            f.Receive(full);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(13));
            Assert.That(f.Verified(13).state.value, Is.EqualTo(130));
            Assert.That(f.probe.lastInput, Is.EqualTo(130));
            Assert.That(f.probe.deltaReads, Is.EqualTo(1),
                "only the newest staged delta needs decoding; intervening ticks replay normally");
        }

        [Test]
        public void LostFullApplicationAckDoesNotStopFreshDeltasOrRegressTheirBaseline()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            f.Receive(full);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            for (ulong tick = 12; tick <= 18; tick++)
            {
                using var delta = f.Prepare(tick, (int)tick * 10);
                Assert.That(delta.full, Is.False);
                Assert.That(delta.baseline, Is.EqualTo(11));
                Assert.That(delta.checkpoint, Is.EqualTo(11));
                Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.EqualTo(11),
                    "fresh unreliable deltas must not allocate another reliable checkpoint");
                Assert.That(f.ServerAck, Is.EqualTo(8));
                f.Receive(delta);
                f.Drain();
                Assert.That(f.ClientAck, Is.EqualTo(tick));
                Assert.That(f.Verified(tick).state.value, Is.EqualTo((int)tick * 10),
                    "decoding against F catches a writer that silently regressed to the old ACK baseline");
            }
            f.DeliverAck();
            using var acknowledged = f.Prepare(19, 190);
            Assert.That(acknowledged.baseline, Is.EqualTo(18));
            Assert.That(acknowledged.checkpoint, Is.EqualTo(11));
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.Zero);
            f.Receive(acknowledged);
            f.Drain();
            Assert.That(f.Verified(19).state.value, Is.EqualTo(190));
        }

        [Test]
        public void FailedFullDoesNotUnlockItsOpaqueContinuation()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            using var continuation = f.Prepare(12, 120);
            f.Receive(continuation);
            f.probe.failFull = true;
            LogAssert.Expect(LogType.Error, new Regex("Discarded prediction record.*checkpoint test full decode failure"));
            f.Receive(full);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(8),
                "a full receipt or partial decode is not an accepted checkpoint");
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.Verified(10).state.value, Is.EqualTo(100));
        }

        [Test]
        public void FailedEnteringStateCaptureRejectsTheDeliveredFullWithoutUnlockingContinuation()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            using var continuation = f.Prepare(12, 120);
            f.Receive(continuation);
            f.probe.failSaveTick = 12;
            LogAssert.Expect(LogType.Error, new Regex(@"Cannot apply prediction checkpoint 11: System\.InvalidOperationException: checkpoint test entering state failure"));
            f.Receive(full);
            f.Drain();
            Assert.That(f.ClientVerified, Is.EqualTo(10));
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(8));
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.client.stagedCheckpointFrames, Is.Zero);
            Assert.That(Get<ulong>(f.client, "_rejectedCheckpointTick"), Is.EqualTo(11));
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
        }

        [Test]
        public void AcceptedNewerCheckpointRejectsLatePreviousEpochWithoutRollingBack()
        {
            using var f = new Fixture();
            using var first = f.Prepare(11, 110, full: true);
            using var oldDelta = f.Prepare(12, 120);
            f.Receive(first);
            f.Drain();
            f.DeliverAck();
            using var replacement = f.Prepare(13, 130, full: true);
            f.Receive(replacement);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(13));
            int inputReads = f.probe.inputReads;
            f.Receive(oldDelta);
            f.Receive(first);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(13));
            Assert.That(f.ClientAck, Is.EqualTo(13));
            Assert.That(f.Verified(13).state.value, Is.EqualTo(130));
            Assert.That(f.probe.inputReads, Is.EqualTo(inputReads));
            Assert.That(f.probe.deltaReads, Is.Zero);
        }

        [TestCase("gap")]
        [TestCase("bytes")]
        [TestCase("expiry")]
        public void StagingBoundsReleaseBytesButRepairWaitsForThePromisedFull(string bound)
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            using var valid = f.Prepare(12, 120);
            ulong rejectedTick = bound == "gap" ? 12UL + f.client.verifiedHistoryWindowTicks : 12UL;
            if (bound == "gap")
                f.ReceiveOpaque(rejectedTick, 11, 1);
            else if (bound == "bytes")
                f.ReceiveOpaque(rejectedTick, 11, PredictionManager.MaxStagedCheckpointBytes + 1);
            else
            {
                f.Receive(valid);
                Assert.That(f.client.stagedCheckpointFrames, Is.EqualTo(1));
                Assert.That(f.client.stagedCheckpointBytes, Is.GreaterThan(0));
                Set(f.client, "_stagedCheckpointReceivedAt", Time.unscaledTimeAsDouble - 3d);
                Invoke(f.client, "ExpireStagedCheckpoint");
            }
            f.Drain();
            Assert.That(f.client.stagedCheckpointFrames, Is.Zero);
            Assert.That(f.client.stagedCheckpointBytes, Is.Zero);
            Assert.That(f.probe.inputReads, Is.Zero);
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.False,
                "a continuation cannot demand replacement of an unreceived reliable full");
            Assert.That(Get<ulong>(f.client, "_checkpointRepairAfterTick"), Is.EqualTo(rejectedTick));

            f.Receive(full);
            f.Drain();
            Assert.That(f.AppliedCheckpoint, Is.EqualTo(11));
            Assert.That(f.ClientAck, Is.EqualTo(11));
            Assert.That(f.probe.deltaReads, Is.Zero);
            Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
            Assert.That(Get<ulong>(f.client, "_historyResyncRequiredAfterTick"), Is.EqualTo(rejectedTick));
        }

        [Test]
        public void NewerWaitingEpochSupersedesOldPayloadAndCleanupResetsItsFence()
        {
            using var f = new Fixture();
            f.ReceiveOpaque(12, 11, 16);
            Assert.That(f.client.stagedCheckpointBytes, Is.EqualTo(16));
            f.ReceiveOpaque(15, 14, 24);
            f.ReceiveOpaque(13, 11, 32);
            Assert.That(f.client.stagedCheckpointFrames, Is.EqualTo(1));
            Assert.That(f.client.stagedCheckpointBytes, Is.EqualTo(24));
            Assert.That(Get<ulong>(f.client, "_waitingCheckpointTick"), Is.EqualTo(14));
            Assert.That(f.probe.inputReads, Is.Zero);
            Assert.That(f.ClientAck, Is.EqualTo(10));
            Invoke(f.client, "ClearCheckpointDelivery");
            Assert.That(f.client.stagedCheckpointFrames, Is.Zero);
            Assert.That(f.client.stagedCheckpointBytes, Is.Zero);
            Assert.That(f.AppliedCheckpoint, Is.Zero);
            Assert.That(Get<ulong>(f.client, "_waitingCheckpointTick"), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyExactReceivedCheckpointProofCanReleaseFullCredit(bool rejected)
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);
            f.Request(11, rejected ? 0UL : 12UL, rejected ? 12UL : 0UL);
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.EqualTo(11));
            Assert.That(f.ServerAck, Is.EqualTo(8), "future proof must not manufacture an application ACK");
            f.Request(11, rejected ? 0UL : 11UL, rejected ? 11UL : 0UL);
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.Zero);
            Assert.That(f.ServerAck, Is.EqualTo(rejected ? 8UL : 11UL));
            Assert.That(f.ServerFrame.lastFullFrameSentTick, Is.EqualTo(11));
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.EqualTo(rejected));
            if (rejected)
            {
                using var replacement = f.Prepare(12, 120);
                Assert.That(replacement.full, Is.True);
                Assert.That(replacement.baseline, Is.EqualTo(11),
                    "rejected proof releases transport credit without regressing the promised baseline");
                Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.EqualTo(12));
                Assert.That(f.ServerAck, Is.EqualTo(8));
            }
        }

        [Test]
        public void OversizedPreparedDeltaDefersWithoutSendingOrConsumingItsDesyncHeal()
        {
            using var f = new Fixture();
            f.PrepareOversizedHealAndAttemptSend();
            Assert.That(f.ServerFrame.preparedFrameTick, Is.Zero);
            Assert.That(f.ServerFrame.requiresFullCheckpoint, Is.True);
            Assert.That(f.ServerFrame.lastFullFrameSentTick, Is.EqualTo(8));
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.Zero);
            Assert.That(f.Server.freshFramesSentTotal, Is.Zero);
            Assert.That(f.Server.reliableFramesSentTotal, Is.Zero);
            Assert.That(f.Server.oversizedFramesDeferredTotal, Is.EqualTo(1));
            Assert.That(f.HasPendingHeal, Is.True);
        }

        [Test]
        public void AckBeyondTheServerTickIsIgnored()
        {
            using var f = new Fixture();
            using var full = f.Prepare(11, 110, full: true);

            f.DeliverAck(ulong.MaxValue);
            Assert.That(f.ServerAck, Is.EqualTo(8), "an ACK for a tick this server never sent cannot become a baseline");
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.EqualTo(11));

            using var delta = f.Prepare(12, 120);
            Assert.That(delta.full, Is.False, "a malformed ACK must not release the pending full or force a reliable full");
            Assert.That(delta.baseline, Is.EqualTo(11));
            Assert.That(f.ServerFrame.reliableFrame.pendingTick, Is.EqualTo(11));

            f.DeliverAck(13);
            Assert.That(f.ServerAck, Is.EqualTo(8), "an ACK ahead of the current server tick is ignored");
            f.DeliverAck(12);
            Assert.That(f.ServerAck, Is.EqualTo(12), "the current tick is the newest frame this server could have sent");
        }

        private sealed class Packet : IDisposable
        {
            public readonly ulong tick, baseline, checkpoint;
            public readonly bool full;
            public readonly BitPacker payload;
            public Packet(ulong tick, ulong baseline, ulong checkpoint, bool full, BitPacker source)
            {
                this.tick = tick; this.baseline = baseline; this.checkpoint = checkpoint; this.full = full;
                payload = Copy(source);
            }
            public void Dispose() => payload.Dispose();
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            private readonly NetworkManager _network;
            private readonly PredictionManager _server;
            private readonly CheckpointProbeIdentity _sender;
            private readonly History<FULL_STATE<CheckpointProbeState>> _verified;
            private readonly List<PlayerPacker> _frames;
            private readonly PredictionManager.InputQueue _input;
            private readonly double _previousCadence;
            private ulong _lastPreparedTick = 8;
            private readonly PlayerID _player = default;
            public readonly PredictionManager client;
            public readonly CheckpointProbeIdentity probe;

            public Fixture()
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                _network = Create<NetworkManager>("Checkpoint client network");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                client = CreateManager("Checkpoint receiver");
                _server = CreateManager("Checkpoint sender");
                Set(typeof(NetworkIdentity), client, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), client, "_isSpawnedClient", false);
                Set(client, "_verifiedServerTick", 10UL);
                Set(client, "_latestFrameServerTick", 10UL);
                Set(client, "_ackedServerTick", 10UL);
                Set(client, "_appliedCheckpointTick", 8UL);
                Set(_server, "<cachedIsServer>k__BackingField", true);
                var id = new PredictedComponentID(new PredictedObjectID(2822), 0);
                probe = Create<CheckpointProbeIdentity>("Checkpoint receiver state");
                _verified = Attach(probe, client, id);
                _sender = Create<CheckpointProbeIdentity>("Checkpoint sender state");
                Attach(_sender, _server, id);
                _frames = Get<List<PlayerPacker>>(_server, "_clientFrames");
                _frames.Add(new PlayerPacker { player = _player, packer = BitPackerPool.Get(),
                    maxUnreliableFrameBytes = 256000, lastFullFrameSentTick = 8 });
                _input = new PredictionManager.InputQueue { ackedServerTick = 8 };
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(_server, "_clientTicks").Add(_player, _input);
            }

            public ulong ClientAck => Get<ulong>(client, "_ackedServerTick");
            public ulong ClientVerified => Get<ulong>(client, "_verifiedServerTick");
            public ulong AppliedCheckpoint => Get<ulong>(client, "_appliedCheckpointTick");
            public ulong ServerAck => _input.ackedServerTick;
            public PlayerPacker ServerFrame => _frames[0];
            public PredictionManager Server => _server;
            public bool HasPendingHeal => Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(
                _server, "_pendingDesyncHeals")[_player].Contains(_sender.id);
            public void Request(ulong failed, ulong applied, ulong rejected)
                => Invoke(_server, "HandleHistoryResyncRequest", _player, failed, applied, rejected);
            public void Drain() => Invoke(client, "ProcessQueuedFrames", true);
            public FULL_STATE<CheckpointProbeState> Verified(ulong tick)
            {
                Assert.That(_verified.Read(tick, out var state), Is.True, $"No verified snapshot at {tick}");
                return state;
            }

            public Packet Prepare(ulong tick, int value, bool full = false, PlayerID? owner = null)
            {
                PrepareInterveningTicks(tick);
                Set(_server, "<localTick>k__BackingField", tick);
                Set(_server, "<localTickInContext>k__BackingField", tick);
                _sender.fullPredictedState = State(value, owner);
                CapturePreparedTick(tick);
                var frame = _frames[0];
                if (full) frame.requiresFullCheckpoint = true;
                _frames[0] = frame;
                Invoke(_server, "WriteInitialFrameToOthers");
                Assert.That(_frames[0].preparedFrameTick, Is.EqualTo(tick), "a fresh frame was suppressed");
                Invoke(_server, "WriteEventHandles");
                frame = _frames[0];
                ulong baseline = frame.preparedBaselineTick;
                ulong checkpoint = frame.fullFrame ? tick : frame.lastFullFrameSentTick;
                var packet = new Packet(tick, baseline, checkpoint, frame.fullFrame, frame.packer);
                if (frame.fullFrame) frame.BeginFullFrame(tick);
                frame.fullFrame = false;
                frame.preparedFrameTick = 0;
                _frames[0] = frame;
                return packet;
            }

            public void Receive(Packet packet)
            {
                var received = Copy(packet.payload);
                try
                {
                    int bytes = received.ToByteData().length;
                    received.ResetPositionAndMode(true);
                    Invoke(client, "HandleFrameFromServer", packet.tick, packet.baseline, packet.checkpoint,
                        packet.tick, packet.full, false, default(PackedInt), false, default(PackedInt),
                        new BitPackerWithLength(bytes, received));
                }
                catch { received.Dispose(); throw; }
            }

            public void ReceiveOpaque(ulong tick, ulong checkpoint, int bytes)
            {
                using var source = BitPackerPool.Get();
                for (int i = 0; i < bytes; i++) Packer<byte>.Write(source, 0xff);
                using var packet = new Packet(tick, checkpoint, checkpoint, false, source);
                Receive(packet);
            }

            public void PrepareOversizedHealAndAttemptSend()
            {
                Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(_server, "_pendingDesyncHeals")
                    .Add(_player, new HashSet<PredictedComponentID> { _sender.id });
                PrepareInterveningTicks(11);
                Set(_server, "<localTick>k__BackingField", 11UL);
                Set(_server, "<localTickInContext>k__BackingField", 11UL);
                _sender.fullPredictedState = State(110);
                CapturePreparedTick(11);
                var frame = _frames[0];
                frame.maxUnreliableFrameBytes = 1;
                _frames[0] = frame;
                Invoke(_server, "WriteInitialFrameToOthers");
                Invoke(_server, "WriteEventHandles");
                Assert.That(_frames[0].fullFrame, Is.False);
                Assert.That(_frames[0].preparedFrameTick, Is.EqualTo(11));
                Assert.That(Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(
                    _server, "_preparedDesyncHeals")[_player], Does.Contain(_sender.id),
                    "the actual serializer must have prepared this heal before deferral");
                Invoke(_server, "SendFrameToOthers");
            }

            public void DeliverAck(ulong? ack = null) => Invoke(_server, "ReceivedInput", 0UL, 0u, ack ?? ClientAck,
                BitPackerPool.Get(), new RPCInfo { sender = _player });

            private void PrepareInterveningTicks(ulong tick)
            {
                for (ulong prepared = _lastPreparedTick + 1; prepared < tick; prepared++)
                {
                    Set(_server, "<localTick>k__BackingField", prepared);
                    Set(_server, "<localTickInContext>k__BackingField", prepared);
                    CapturePreparedTick(prepared);
                }
            }

            private void CapturePreparedTick(ulong tick)
            {
                Invoke(_server, "CaptureInputHistory", tick);
                Invoke(_server, "CaptureLifecycleHistory", tick);
                _lastPreparedTick = tick;
            }

            private T Create<T>(string name) where T : Component
            {
                var go = new GameObject(name);
                _objects.Add(go);
                return go.AddComponent<T>();
            }

            private PredictionManager CreateManager(string name)
            {
                var manager = Create<PredictionManager>(name);
                Set(manager, "<tickRate>k__BackingField", 60);
                Set(manager, "<tickDelta>k__BackingField", 1f / 60);
                Set(manager, "<localTick>k__BackingField", 40UL);
                Set(manager, "<localTickInContext>k__BackingField", 40UL);
                Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(manager, "_updateViewMode", UpdateViewMode.None);
                return manager;
            }

            private static History<FULL_STATE<CheckpointProbeState>> Attach(
                CheckpointProbeIdentity identity, PredictionManager manager, PredictedComponentID id)
            {
                identity.Attach(manager, id);
                identity.fullPredictedState = State(900);
                var predicted = new History<FULL_STATE<CheckpointProbeState>>(200);
                predicted.Write(0, State(900));
                Set(typeof(PredictedIdentity<CheckpointProbeState>), identity, "_stateHistory", predicted);
                var verified = manager.GetVerifiedHistory<FULL_STATE<CheckpointProbeState>>(id, out _);
                verified.Write(8, State(80));
                verified.Write(10, State(100));
                Set(typeof(PredictedIdentity<CheckpointProbeState>), identity, "_verifiedHistory", verified);
                identity.lastVerifiedTick = 10;
                Get<List<PredictedIdentity>>(manager, "_systems").Add(identity);
                Set(manager, "_systemsCount", 1);
                Set(manager, "_inputHistorySystems", 1);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Add(id, identity);
                return verified;
            }

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                // Manager cleanup owns queued/staged packet lifetimes. Detach the probes first
                // so that Editor-only lifecycle callbacks cannot hide missing state disposal.
                foreach (var manager in new[] { client, _server })
                {
                    Get<List<PredictedIdentity>>(manager, "_systems").Clear();
                    Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Clear();
                    Set(manager, "_systemsCount", 0);
                }
                probe.ReleasePredictionStateForPool();
                _sender.ReleasePredictionStateForPool();
                Invoke(client, "CleanupAllSystems");
                Invoke(_server, "CleanupAllSystems");
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                for (int i = _objects.Count - 1; i >= 0; i--) Object.DestroyImmediate(_objects[i]);
            }
        }

        private static FULL_STATE<CheckpointProbeState> State(int value, PlayerID? owner = null)
        {
            var result = new FULL_STATE<CheckpointProbeState> { state = new CheckpointProbeState { value = value } };
            result.prediction.wasOnSimulationStartCalled = true;
            result.prediction.owner = owner;
            return result;
        }

        private static BitPacker Copy(BitPacker source)
        {
            int end = source.positionInBits;
            int bytes = source.ToByteData().length;
            var copy = BitPackerPool.Get();
            source.ResetPositionAndMode(true);
            copy.WriteBits(source, bytes * 8);
            source.SetBitPosition(end);
            return copy;
        }

        private static T Get<T>(PredictionManager manager, string name) => (T)Field(typeof(PredictionManager), name).GetValue(manager);
        private static void Set(PredictionManager manager, string name, object value) => Set(typeof(PredictionManager), manager, name, value);
        private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
        private static FieldInfo Field(Type type, string name)
        {
            var field = type.GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return field;
        }
        private static object Invoke(PredictionManager manager, string name, params object[] args)
        {
            var method = typeof(PredictionManager).GetMethod(name, Fields);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            return method.Invoke(manager, args);
        }
    }

    public struct CheckpointProbeState : IPredictedData<CheckpointProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class CheckpointProbeIdentity : PredictedIdentity<CheckpointProbeState>
    {
        public int deltaReads, inputReads, lastInput;
        public bool failFull;
        public ulong failSaveTick;
        public override bool hasInput => true;
        internal override bool HasInputAt(ulong tick) => true;
        public override void WriteFirstInput(ulong tick, BitPacker packer) => Packer<int>.Write(packer, (int)tick * 10);
        public override void ReadFirstInput(ulong tick, BitPacker packer)
        {
            inputReads++;
            lastInput = Packer<int>.Read(packer);
        }
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager; id = componentId; myType = GetType();
        }
        protected override void Simulate(ref CheckpointProbeState state, float delta) => state.value++;
        protected override void WriteDeltaState(BitPacker packer, in CheckpointProbeState baseline, in CheckpointProbeState current)
            => Packer<int>.Write(packer, current.value - baseline.value);
        protected override void ReadDeltaState(BitPacker packer, in CheckpointProbeState baseline, ref CheckpointProbeState state)
        {
            deltaReads++;
            state.value = baseline.value + Packer<int>.Read(packer);
        }
        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            if (failFull) throw new InvalidOperationException("checkpoint test full decode failure");
            base.ReadFirstState(tick, packer, serverTick);
        }
        internal override void SaveStateInHistory(ulong tick)
        {
            if (tick == failSaveTick) throw new InvalidOperationException("checkpoint test entering state failure");
            base.SaveStateInHistory(tick);
        }
    }
}
