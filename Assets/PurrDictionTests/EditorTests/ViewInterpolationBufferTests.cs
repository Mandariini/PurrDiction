using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ViewInterpolationBufferTests
    {
        private struct Sample : IDisposable
        {
            public float value;

            public void Dispose() { }

            public static Sample Of(float v) => new Sample { value = v };
        }

        private static Sample Lerp(Sample from, Sample to, float t)
            => Sample.Of(from.value + (to.value - from.value) * t);

        private static InterpolatedWithDispose<Sample> CreateBuffer(int tickRate)
        {
            return new InterpolatedWithDispose<Sample>(
                Lerp,
                1f / tickRate,
                Sample.Of(0f),
                PredictionManager.GetViewInterpolationMaxBufferSize(tickRate));
        }

        [Test]
        public void MaxBufferSizeIsTimeAnchoredWithAFloorOfThree()
        {
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(10), Is.EqualTo(3));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(20), Is.EqualTo(3));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(29), Is.EqualTo(3));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(30), Is.EqualTo(3));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(32), Is.EqualTo(3));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(40), Is.EqualTo(4));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(64), Is.EqualTo(6));
            Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(128), Is.EqualTo(12));
        }

        [Test]
        public void MaxBufferSizeMatchesLegacyFormulaAtThirtyTicksAndAbove()
        {
            for (int tickRate = 30; tickRate <= 128; tickRate++)
            {
                int legacy = (int)MathF.Max(tickRate / 10f, 2f);
                Assert.That(PredictionManager.GetViewInterpolationMaxBufferSize(tickRate),
                    Is.EqualTo(legacy), $"tickRate={tickRate}");
            }
        }

        [Test]
        public void StandingBufferDepthStaysMinimal()
        {
            var buffer = CreateBuffer(20);
            Assert.That(buffer.minBufferSize, Is.EqualTo(1));
            Assert.That(buffer.maxBufferSize, Is.EqualTo(3));

            var buffer64 = CreateBuffer(64);
            Assert.That(buffer64.minBufferSize, Is.EqualTo(1));
            Assert.That(buffer64.maxBufferSize, Is.EqualTo(6));
        }

        [Test]
        public void SteadyCadenceViewLagsAtMostOneTick()
        {
            const int tickRate = 20;
            const float tickDelta = 1f / tickRate;
            const int subSteps = 5;
            var buffer = CreateBuffer(tickRate);

            float view = 0f;
            for (int tick = 1; tick <= 200; tick++)
            {
                buffer.Add(Sample.Of(tick));
                for (int s = 0; s < subSteps; s++)
                    view = buffer.Advance(tickDelta / subSteps).value;

                if (tick > 5)
                {
                    Assert.That(view, Is.GreaterThanOrEqualTo(tick - 1.2f), $"tick={tick}");
                    Assert.That(view, Is.LessThanOrEqualTo(tick + 0.001f), $"tick={tick}");
                }
            }
        }

        [Test]
        public void DriftedBufferKeepsAdvancingAtLowTickrateSizing()
        {
            const int tickRate = 20;
            const float tickDelta = 1f / tickRate;
            const int subSteps = 5;
            var buffer = CreateBuffer(tickRate);

            float view = 0f;
            for (int tick = 1; tick <= 20; tick++)
            {
                buffer.Add(Sample.Of(tick));
                for (int s = 0; s < subSteps; s++)
                    view = buffer.Advance(tickDelta / subSteps).value;
            }

            buffer.Add(Sample.Of(20.5f));

            float previousView = view;
            for (int tick = 21; tick <= 220; tick++)
            {
                buffer.Add(Sample.Of(tick));
                for (int s = 0; s < subSteps; s++)
                    view = buffer.Advance(tickDelta / subSteps).value;

                Assert.That(view, Is.GreaterThanOrEqualTo(previousView - 0.001f), $"tick={tick}");
                previousView = view;

                if (tick > 30)
                    Assert.That(tick - view, Is.LessThanOrEqualTo(2.5f), $"tick={tick}");
            }
        }

        [Test]
        public void ViewRecoversAfterTickBurst()
        {
            const int tickRate = 20;
            const float tickDelta = 1f / tickRate;
            const int subSteps = 5;
            var buffer = CreateBuffer(tickRate);

            float view = 0f;
            for (int tick = 1; tick <= 20; tick++)
            {
                buffer.Add(Sample.Of(tick));
                for (int s = 0; s < subSteps; s++)
                    view = buffer.Advance(tickDelta / subSteps).value;
            }

            buffer.Add(Sample.Of(24));
            view = buffer.Advance(tickDelta * 4).value;

            for (int tick = 25; tick <= 60; tick++)
            {
                buffer.Add(Sample.Of(tick));
                for (int s = 0; s < subSteps; s++)
                    view = buffer.Advance(tickDelta / subSteps).value;

                if (tick > 28)
                {
                    Assert.That(view, Is.GreaterThanOrEqualTo(tick - 2.5f), $"tick={tick}");
                    Assert.That(view, Is.LessThanOrEqualTo(tick + 0.001f), $"tick={tick}");
                }
            }
        }
    }
}
