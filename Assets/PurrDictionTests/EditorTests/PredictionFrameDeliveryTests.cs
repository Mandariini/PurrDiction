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

        [Test]
        public void ReliableRecoveryWindowScalesWithTickRateAboveTheInputWindow()
        {
            Assert.That(PredictionManager.ReliableRecoveryWindowTicks(20), Is.EqualTo(32));
            Assert.That(PredictionManager.ReliableRecoveryWindowTicks(64), Is.EqualTo(32));
            Assert.That(PredictionManager.ReliableRecoveryWindowTicks(128), Is.EqualTo(64));
        }

        [Test]
        public void HealthyBaselineAdvanceNeverEntersDistress()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;
            const ulong latencyTicks = 20;

            for (ulong tick = 1; tick <= 200; tick++)
            {
                ulong baseline = tick > latencyTicks ? tick - latencyTicks : 0;
                tracker.Observe(tick, baseline, window);
                Assert.That(tracker.distressed, Is.False, $"tick {tick}");
            }
        }

        [Test]
        public void StalledBaselineEntersDistressWithinTwoWindows()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;
            const ulong stallBaseline = 100;

            for (ulong tick = 101; tick <= 101 + window * 2; tick++)
                tracker.Observe(tick, stallBaseline, window);

            Assert.That(tracker.distressed, Is.True);
        }

        [Test]
        public void SlowCrawlingBaselineEntersDistress()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;

            ulong baseline = 100;
            for (ulong tick = 101; tick <= 101 + window * 3; tick++)
            {
                if (tick % 4 == 0)
                    baseline++;
                tracker.Observe(tick, baseline, window);
            }

            Assert.That(tracker.distressed, Is.True);
        }

        [Test]
        public void RecoveredBaselineAdvanceExitsDistress()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;

            for (ulong tick = 101; tick <= 101 + window * 2; tick++)
                tracker.Observe(tick, 100, window);
            Assert.That(tracker.distressed, Is.True);

            ulong start = 101 + window * 2;
            for (ulong tick = start + 1; tick <= start + window * 2; tick++)
                tracker.Observe(tick, tick - 10, window);

            Assert.That(tracker.distressed, Is.False);
        }

        [Test]
        public void UnackedClientNeverEntersDistress()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;

            for (ulong tick = 1; tick <= window * 4; tick++)
                tracker.Observe(tick, 0, window);

            Assert.That(tracker.distressed, Is.False);
        }

        [Test]
        public void BaselineRegressionResetsTheObservationWindow()
        {
            var tracker = new BaselineAdvanceTracker();
            const ulong window = 32;

            for (ulong tick = 101; tick <= 120; tick++)
                tracker.Observe(tick, 100, window);

            tracker.Observe(121, 0, window);

            for (ulong tick = 122; tick <= 122 + window; tick++)
                tracker.Observe(tick, tick - 5, window);

            Assert.That(tracker.distressed, Is.False);
        }
    }
}
