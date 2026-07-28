using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class TickPacingControllerTests
    {
        [Test]
        public void SlackTargetIsBaseWithoutJitter()
        {
            Assert.That(PredictionManager.ComputeSlackTargetMs(0d),
                Is.EqualTo(PredictionManager.SlackTargetBaseMs).Within(1e-9));
        }

        [Test]
        public void SlackTargetGrowsWithJitterHeadroom()
        {
            var expected = PredictionManager.SlackTargetBaseMs + PredictionManager.SlackJitterHeadroom * 20d;
            Assert.That(PredictionManager.ComputeSlackTargetMs(20d), Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void SlackTargetIgnoresNegativeDeviation()
        {
            Assert.That(PredictionManager.ComputeSlackTargetMs(-30d),
                Is.EqualTo(PredictionManager.SlackTargetBaseMs).Within(1e-9));
        }

        [Test]
        public void SlackTargetIsCapped()
        {
            Assert.That(PredictionManager.ComputeSlackTargetMs(100000d),
                Is.EqualTo(PredictionManager.SlackTargetMaxMs).Within(1e-9));
            Assert.That(PredictionManager.ComputeSlackTargetMs(0d, 100000d),
                Is.EqualTo(PredictionManager.SlackTargetMaxMs).Within(1e-9));
        }

        [Test]
        public void SlackTargetCoversObservedDroop()
        {
            var expected = PredictionManager.SlackTargetBaseMs + 30d + PredictionManager.SlackDroopMarginMs;
            Assert.That(PredictionManager.ComputeSlackTargetMs(0d, 30d), Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void SlackTargetUsesTheLargerOfJitterAndDroopHeadroom()
        {
            var jitterDominated = PredictionManager.SlackTargetBaseMs + PredictionManager.SlackJitterHeadroom * 30d;
            Assert.That(PredictionManager.ComputeSlackTargetMs(30d, 10d), Is.EqualTo(jitterDominated).Within(1e-9));

            var droopDominated = PredictionManager.SlackTargetBaseMs + 60d + PredictionManager.SlackDroopMarginMs;
            Assert.That(PredictionManager.ComputeSlackTargetMs(10d, 60d), Is.EqualTo(droopDominated).Within(1e-9));
        }

        [Test]
        public void PacingIsNeutralInsideDeadband()
        {
            Assert.That(PredictionManager.ComputeTickPacingScale(12d, 12d), Is.EqualTo(1d));
            Assert.That(PredictionManager.ComputeTickPacingScale(12d + PredictionManager.SlackDeadbandMs, 12d), Is.EqualTo(1d));
            Assert.That(PredictionManager.ComputeTickPacingScale(12d - PredictionManager.SlackDeadbandMs, 12d), Is.EqualTo(1d));
        }

        [Test]
        public void ExcessSlackSlowsTheClock()
        {
            var scale = PredictionManager.ComputeTickPacingScale(50d, 12d);
            Assert.That(scale, Is.GreaterThan(1d));

            var expected = 1d + (50d - 12d - PredictionManager.SlackDeadbandMs) * PredictionManager.SlackScalePerMs;
            Assert.That(scale, Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void SlackDeficitSpeedsUpTheClock()
        {
            var scale = PredictionManager.ComputeTickPacingScale(-30d, 12d);
            Assert.That(scale, Is.LessThan(1d));

            var expected = 1d - (42d - PredictionManager.SlackDeadbandMs) * PredictionManager.SlackScalePerMs;
            Assert.That(scale, Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void PacingOffsetIsClampedBothWays()
        {
            Assert.That(PredictionManager.ComputeTickPacingScale(5000d, 12d),
                Is.EqualTo(1d + PredictionManager.MaxTickPacingOffset).Within(1e-12));
            Assert.That(PredictionManager.ComputeTickPacingScale(-5000d, 12d),
                Is.EqualTo(1d - PredictionManager.MaxTickPacingOffset).Within(1e-12));
        }

        [Test]
        public void LargerErrorNeverProducesSmallerCorrection()
        {
            double previous = PredictionManager.ComputeTickPacingScale(0d, 12d);
            for (var slack = 1d; slack <= 100d; slack += 1d)
            {
                var scale = PredictionManager.ComputeTickPacingScale(slack, 12d);
                Assert.That(scale, Is.GreaterThanOrEqualTo(previous));
                previous = scale;
            }
        }

        [Test]
        public void LegacyJumpThresholdWithoutSlackFeedback()
        {
            Assert.That(PredictionManager.ShouldJumpForLowMargin(0, 2, false, 0), Is.True);
            Assert.That(PredictionManager.ShouldJumpForLowMargin(-3, 2, false, 0), Is.True);
            Assert.That(PredictionManager.ShouldJumpForLowMargin(1, 2, false, 0), Is.False);
        }

        [Test]
        public void SlackModeToleratesZeroMargin()
        {
            Assert.That(PredictionManager.ShouldJumpForLowMargin(0, 2, true, 0), Is.False);
            Assert.That(PredictionManager.ShouldJumpForLowMargin(1, 2, true, 0), Is.False);
        }

        [Test]
        public void SlackModeRequiresSustainedLateness()
        {
            Assert.That(PredictionManager.ShouldJumpForLowMargin(-1, 2, true, 1), Is.False);
            Assert.That(PredictionManager.ShouldJumpForLowMargin(-1, 2, true, PredictionManager.LowMarginJumpStreak), Is.True);
        }

        [Test]
        public void SlackModeJumpsImmediatelyWhenDeeplyLate()
        {
            Assert.That(PredictionManager.ShouldJumpForLowMargin(-2, 2, true, 1), Is.True);
            Assert.That(PredictionManager.ShouldJumpForLowMargin(-10, 2, true, 0), Is.True);
        }

        [Test]
        public void SlowingIsBlockedAtTheMinimumLeadFloor()
        {
            Assert.That(PredictionManager.ClampPacingScaleForLead(1.02d, 2, 2), Is.EqualTo(1d));
            Assert.That(PredictionManager.ClampPacingScaleForLead(1.02d, 1, 2), Is.EqualTo(1d));
        }

        [Test]
        public void SlowingIsAllowedAboveTheMinimumLeadFloor()
        {
            Assert.That(PredictionManager.ClampPacingScaleForLead(1.02d, 3, 2), Is.EqualTo(1.02d));
        }

        [Test]
        public void SpeedingUpIsNeverLeadClamped()
        {
            Assert.That(PredictionManager.ClampPacingScaleForLead(0.98d, 1, 2), Is.EqualTo(0.98d));
            Assert.That(PredictionManager.ClampPacingScaleForLead(0.98d, 5, 2), Is.EqualTo(0.98d));
        }
    }
}
