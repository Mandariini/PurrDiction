using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class FullCheckpointDeliveryTests
    {
        [Test]
        public void DuplicateObserverSyncPreservesPendingFullAckQueueAndSentVisibility()
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var networkObject = new GameObject("Duplicate observer network");
            var predictionObject = new GameObject("Duplicate observer prediction");
            networkObject.SetActive(false);
            predictionObject.SetActive(false);
            PredictionManager manager = null;
            try
            {
                var transport = networkObject.AddComponent<LocalTransport>();
                var network = networkObject.AddComponent<NetworkManager>();
                network.transport = transport;
                typeof(NetworkManager).GetField("_serverPlayersManager", fields).SetValue(
                    network, new PlayersManager(network, null, null));
                manager = predictionObject.AddComponent<PredictionManager>();
                typeof(NetworkIdentity).GetField("<networkManager>k__BackingField", fields).SetValue(manager, network);

                var player = default(PlayerID);
                var frame = new PlayerPacker
                {
                    player = player, packer = BitPackerPool.Get(),
                    preparedFrameTick = 111, preparedBaselineTick = 100,
                    preparedVisibilityTick = 111, sentVisibilityTick = 110,
                    maxUnreliableFrameBytes = 1
                };
                frame.BeginFullFrame(100);
                var frames = (List<PlayerPacker>)typeof(PredictionManager).GetField("_clientFrames", fields).GetValue(manager);
                frames.Add(frame);
                var input = new PredictionManager.InputQueue { ackedServerTick = 90, lastConsumedTick = 85 };
                var inputs = (Dictionary<PlayerID, PredictionManager.InputQueue>)typeof(PredictionManager)
                    .GetField("_clientTicks", fields).GetValue(manager);
                inputs.Add(player, input);
                var cooldowns = (Dictionary<PlayerID, double>)typeof(PredictionManager)
                    .GetField("_historyResyncServedAt", fields).GetValue(manager);
                cooldowns.Add(player, 7d);
                var pending = (List<PlayerID>)typeof(PredictionManager).GetField("_pendingFullSync", fields).GetValue(manager);
                pending.Add(player);
                pending.Add(player);

                typeof(PredictionManager).GetMethod("FlushPendingFullSyncs", fields).Invoke(manager, null);

                Assert.That(frames.Count, Is.EqualTo(1));
                Assert.That(pending, Is.Empty);
                Assert.That(inputs[player], Is.SameAs(input));
                Assert.That(input.ackedServerTick, Is.EqualTo(90));
                Assert.That(input.lastConsumedTick, Is.EqualTo(85));
                Assert.That(cooldowns[player], Is.EqualTo(7d));
                var retained = frames[0];
                Assert.That(retained.reliableFrame.pendingTick, Is.EqualTo(100));
                Assert.That(retained.reliableSentAtLocalTick, Is.EqualTo(100));
                Assert.That(retained.lastFullFrameSentTick, Is.EqualTo(100));
                Assert.That(retained.sentVisibilityTick, Is.EqualTo(110));
                Assert.That(retained.requiresFullCheckpoint, Is.True);
                Assert.That(retained.preparedFrameTick, Is.Zero);
                Assert.That(retained.preparedBaselineTick, Is.Zero);
                Assert.That(retained.preparedVisibilityTick, Is.Zero);
                Assert.That(retained.maxUnreliableFrameBytes,
                    Is.EqualTo(PredictionManager.GetMaxUnreliableFrameBytes(500)));
                Assert.That(retained.packer, Is.SameAs(frame.packer));

            }
            finally
            {
                // Inactive components may never run OnDestroy; release their owned state explicitly.
                if (manager)
                {
                    var frames = (List<PlayerPacker>)typeof(PredictionManager)
                        .GetField("_clientFrames", fields).GetValue(manager);
                    foreach (var existing in frames)
                        existing.Dispose();
                    frames.Clear();
                }
                UnityEngine.Object.DestroyImmediate(predictionObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [TestCase(100UL)]
        [TestCase(125UL)]
        public void CheckpointOrFencedContinuationAckReleasesOneFullSlot(ulong ack)
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);

            Assert.That(frame.reliableFrame.IsPending(ack), Is.False);
            frame.ClearRecoveryFrame();

            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100),
                "releasing the full slot must preserve the epoch of subsequent deltas");
            frame.BeginFullFrame(126);
            Assert.That(frame.reliableFrame.pendingTick, Is.EqualTo(126));
            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(126));
        }

        [Test]
        public void PendingFullCannotBeOverwrittenByAnotherFull()
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);

            Assert.Throws<InvalidOperationException>(() => frame.BeginFullFrame(120));

            Assert.That(frame.reliableFrame.pendingTick, Is.EqualTo(100));
            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100));
            Assert.That(frame.reliableSentAtLocalTick, Is.EqualTo(100));
        }

        [Test]
        public void DuplicateOldAckCannotReleaseTheNextFullCheckpoint()
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);
            Assert.That(frame.reliableFrame.IsPending(100), Is.False);
            frame.ClearRecoveryFrame();
            frame.BeginFullFrame(120);

            Assert.That(frame.reliableFrame.IsPending(100), Is.True);
            Assert.That(frame.reliableFrame.IsPending(119), Is.True);
            Assert.That(frame.reliableFrame.pendingTick, Is.EqualTo(120));
            Assert.That(frame.reliableFrame.IsPending(120), Is.False);
            Assert.That(frame.reliableFrame.IsPending(120), Is.False);
        }

        [Test]
        public void UnchangedAckNeverExpiresThePendingFull()
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);

            for (var i = 0; i < 10000; i++)
                Assert.That(frame.reliableFrame.IsPending(99), Is.True);

            Assert.That(frame.reliableFrame.pendingTick, Is.EqualTo(100),
                "elapsed polling cannot enqueue additional full snapshots behind one reliable RPC");
        }

        [Test]
        public void ClearingDeliveredFullReleasesPendingTickAndRetainsItsEpoch()
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);
            Assert.That(frame.reliableFrame.IsPending(100), Is.False);

            frame.ClearRecoveryFrame();

            Assert.That(frame.reliableFrame.pendingTick, Is.Zero);
            Assert.That(frame.reliableSentAtLocalTick, Is.Zero);
            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100));
        }

        [Test]
        public void AcknowledgingPreviousFullPreservesCoalescedRequestUntilItsReplacementIsSent()
        {
            var frame = new PlayerPacker();
            frame.BeginFullFrame(100);
            frame.requiresFullCheckpoint = true;
            frame.preparedBaselineTick = 100;
            Assert.That(frame.reliableFrame.IsPending(100), Is.False);

            frame.ClearRecoveryFrame();

            Assert.That(frame.requiresFullCheckpoint, Is.True);
            Assert.That(frame.preparedBaselineTick, Is.EqualTo(100));
            frame.BeginFullFrame(120);
            Assert.That(frame.requiresFullCheckpoint, Is.False);
            Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(120));
        }

        [Test]
        public void PreparingContinuationReusesWorkingPackerWithoutChangingCheckpointMetadata()
        {
            var frame = new PlayerPacker { packer = BitPackerPool.Get() };
            try
            {
                frame.packer.WriteBytes(new byte[] { 0x7A, 0x00, 0xFF, 0xB3 });
                frame.BeginFullFrame(100);
                var originalPacker = frame.packer;

                // The RPC has serialized the full. Its working buffer is immediately
                // available for a fresh continuation while the checkpoint stays pending.
                frame.packer.ResetPositionAndMode(false);
                var continuation = new byte[] { 0xC1, 0x02 };
                frame.packer.WriteBytes(continuation);

                Assert.That(frame.packer, Is.SameAs(originalPacker));
                Assert.That(frame.packer.ToByteData().span.ToArray(), Is.EqualTo(continuation));
                Assert.That(frame.reliableFrame.IsPending(99), Is.True);
                Assert.That(frame.reliableSentAtLocalTick, Is.EqualTo(100));
                Assert.That(frame.lastFullFrameSentTick, Is.EqualTo(100));
            }
            finally
            {
                frame.Dispose();
            }
        }

        [Test]
        public void DisposeReleasesPackerCreditAndEpoch()
        {
            var frame = new PlayerPacker { packer = BitPackerPool.Get() };
            frame.BeginFullFrame(100);
            frame.requiresFullCheckpoint = true;
            frame.preparedBaselineTick = 100;

            frame.Dispose();

            Assert.That(frame.packer, Is.Null);
            Assert.That(frame.reliableFrame.pendingTick, Is.Zero);
            Assert.That(frame.lastFullFrameSentTick, Is.Zero);
            Assert.That(frame.requiresFullCheckpoint, Is.False);
            Assert.That(frame.preparedBaselineTick, Is.Zero);
            Assert.DoesNotThrow(() => frame.Dispose());
        }

        [Test]
        public void ZeroTickCannotBeginAFullCheckpoint()
        {
            var frame = new PlayerPacker();

            Assert.Throws<ArgumentOutOfRangeException>(() => frame.BeginFullFrame(0));
            Assert.That(frame.reliableFrame.pendingTick, Is.Zero);
            Assert.That(frame.lastFullFrameSentTick, Is.Zero);
        }
    }
}
