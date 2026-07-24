using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionFrameDeliveryTests
    {
        [Test]
        public void ReliableFrameSuppressesUntilItsTickIsAcknowledged()
        {
            var state = new ReliableFrameDeliveryState();

            Assert.That(state.ShouldSuppress(0), Is.False);

            state.MarkSent(42);

            Assert.That(state.ShouldSuppress(0), Is.True);
            Assert.That(state.ShouldSuppress(41), Is.True);
            Assert.That(state.ShouldSuppress(42), Is.False);
            Assert.That(state.ShouldSuppress(0), Is.False);
        }

        [Test]
        public void ClearResetsReliableFrameEpoch()
        {
            var state = new ReliableFrameDeliveryState();
            state.MarkSent(42);

            state.Clear();

            Assert.That(state.ShouldSuppress(0), Is.False);
        }

        [Test]
        public void FragmentLimitRoutesOnlyOversizedAndFullFramesReliably()
        {
            const int mtu = 1023;
            const int expectedMaxFrameBytes = 256191;

            var maxFrameBytes = PredictionManager.GetMaxUnreliableFrameBytes(mtu);

            Assert.That(maxFrameBytes, Is.EqualTo(expectedMaxFrameBytes));
            Assert.That(PredictionManager.RequiresReliableRecovery(false, maxFrameBytes, maxFrameBytes), Is.False);
            Assert.That(PredictionManager.RequiresReliableRecovery(false, maxFrameBytes + 1, maxFrameBytes), Is.True);
            Assert.That(PredictionManager.RequiresReliableRecovery(true, 0, maxFrameBytes), Is.True);
        }
    }
}
