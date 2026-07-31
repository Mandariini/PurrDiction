using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictedPlayersStateSerializationTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            NetworkManager.CallAllRegisters();
        }

        [Test]
        public void EmptyBaselineToOnePlayerDeltaSurvivesSharedSnapshots()
        {
            var player = new PlayerID(new PackedULong(42), false);
            var baseline = EmptyState();
            var authoritative = baseline.Duplicate();
            var received = baseline.Duplicate();

            try
            {
                authoritative.players.Add(player);

                Assert.That(baseline.players.Count, Is.Zero,
                    "mutating the authoritative copy must not mutate its shared baseline");
                Assert.That(authoritative.players.Count, Is.EqualTo(1));
                Assert.That(DeltaPacker<PredictedPlayersState>.HasPacker(), Is.True,
                    "PredictedPlayersState must use its generated delta serializer");

                using var packer = BitPackerPool.Get();
                Assert.That(DeltaPacker<PredictedPlayersState>.Write(
                    packer, baseline, authoritative), Is.True);
                int payloadBits = packer.positionInBits;
                packer.ResetPositionAndMode(true);

                DeltaPacker<PredictedPlayersState>.Read(
                    packer, baseline, ref received);

                Assert.That(received.players.isDisposed, Is.False);
                Assert.That(received.players.Count, Is.EqualTo(1));
                Assert.That(received.players[0], Is.EqualTo(player));
                Assert.That(baseline.players.Count, Is.Zero,
                    "reading into a shared snapshot must not mutate the baseline");
                Assert.That(packer.positionInBits, Is.EqualTo(payloadBits),
                    "the delta reader must consume exactly the payload it was given");
            }
            finally
            {
                received.Dispose();
                authoritative.Dispose();
                baseline.Dispose();
            }
        }

        [Test]
        public void PredictedPlayersStateFullRoundTripPreservesPlayers()
        {
            var first = new PlayerID(new PackedULong(7), false);
            var second = new PlayerID(new PackedULong(19), false);
            var authoritative = EmptyState();
            PredictedPlayersState received = default;

            try
            {
                authoritative.players.Add(first);
                authoritative.players.Add(second);

                using var packer = BitPackerPool.Get();
                Packer<PredictedPlayersState>.Write(packer, authoritative);
                int payloadBits = packer.positionInBits;
                packer.ResetPositionAndMode(true);
                Packer<PredictedPlayersState>.Read(packer, ref received);

                Assert.That(received.players.isDisposed, Is.False);
                Assert.That(received.players.Count, Is.EqualTo(2));
                Assert.That(received.players[0], Is.EqualTo(first));
                Assert.That(received.players[1], Is.EqualTo(second));
                Assert.That(packer.positionInBits, Is.EqualTo(payloadBits),
                    "the full-state reader must consume exactly the payload it was given");
            }
            finally
            {
                received.Dispose();
                authoritative.Dispose();
            }
        }

        private static PredictedPlayersState EmptyState()
        {
            return new PredictedPlayersState
            {
                players = DisposableList<PlayerID>.Create(16)
            };
        }
    }
}
