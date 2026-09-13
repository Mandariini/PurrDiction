using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionFrameDeliveryTests
    {
        [Test]
        public void FullCheckpointRemainsPendingUntilItsTickIsAcknowledged()
        {
            var state = new ReliableFrameDeliveryState();

            Assert.That(state.IsPending(0), Is.False);

            state.MarkSent(42);

            Assert.That(state.IsPending(0), Is.True);
            Assert.That(state.IsPending(41), Is.True);
            Assert.That(state.IsPending(42), Is.False);
            Assert.That(state.IsPending(0), Is.False);
        }

        [Test]
        public void ClearReleasesFullCheckpointCredit()
        {
            var state = new ReliableFrameDeliveryState();
            state.MarkSent(42);

            state.Clear();

            Assert.That(state.IsPending(0), Is.False);
        }

        [Test]
        public void UnreliableFragmentCeilingReservesFrameProtocolOverhead()
        {
            const int mtu = 1023;
            const int expectedMaxFrameBytes = 256181;

            Assert.That(PredictionManager.GetMaxUnreliableFrameBytes(mtu),
                Is.EqualTo(expectedMaxFrameBytes));
        }

        [Test]
        public void InputRedundancyTracksTheInputMarginHighBand()
        {
            Assert.That(PredictionManager.InputRedundancyTicks(20), Is.EqualTo(5));
            Assert.That(PredictionManager.InputRedundancyTicks(64), Is.EqualTo(7));
            Assert.That(PredictionManager.InputRedundancyTicks(128), Is.EqualTo(13));
            Assert.That(PredictionManager.InputRedundancyTicks(400), Is.EqualTo(32));

            Assert.That(
                PredictionManager.InputRedundancyTicks(100),
                Is.EqualTo((ulong)(2 * PredictionManager.InputMarginTargetTicks(100) + 1)));
        }
    }
}
