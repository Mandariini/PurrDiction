using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictedViewBufferTests
    {
        private sealed class Tracked : IDisposable
        {
            public readonly float value;
            public bool disposed;

            public Tracked(float value) => this.value = value;

            public void Dispose() => disposed = true;
        }

        private static readonly List<Tracked> _lerpResults = new();

        private static Tracked Lerp(Tracked from, Tracked to, float t)
        {
            var result = new Tracked(from.value + (to.value - from.value) * t);
            _lerpResults.Add(result);
            return result;
        }

        private static PredictedViewBuffer<Tracked> Create(ulong tick, float value, int capacity = 8)
            => new PredictedViewBuffer<Tracked>(Lerp, tick, new Tracked(value), capacity);

        [Test]
        public void PresentsTheFractionBetweenTwoTaggedSamples()
        {
            var buffer = Create(10, 100f);
            buffer.Add(11, new Tracked(110f));
            buffer.Add(12, new Tracked(120f));

            Assert.That(buffer.Sample(10.25d).value, Is.EqualTo(102.5f).Within(1e-4));
            Assert.That(buffer.Sample(11d).value, Is.EqualTo(110f).Within(1e-4));
            Assert.That(buffer.Sample(11.5d).value, Is.EqualTo(115f).Within(1e-4));
            Assert.That(buffer.Count, Is.EqualTo(1));
        }

        [Test]
        public void SpansASkippedTickInsteadOfSnapping()
        {
            var buffer = Create(10, 100f);
            buffer.Add(13, new Tracked(130f));

            Assert.That(buffer.Sample(11d).value, Is.EqualTo(110f).Within(1e-4));
            Assert.That(buffer.Sample(12.5d).value, Is.EqualTo(125f).Within(1e-4));
        }

        [Test]
        public void HoldsTheNewestSampleWhenTheClockRunsAhead()
        {
            var buffer = Create(10, 100f);
            buffer.Add(11, new Tracked(110f));

            Assert.That(buffer.Sample(14d).value, Is.EqualTo(110f).Within(1e-4));
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(buffer.anchorTick, Is.EqualTo(11UL));

            buffer.Add(15, new Tracked(150f));
            Assert.That(buffer.Sample(14.5d).value, Is.EqualTo(145f).Within(1e-4));
        }

        [Test]
        public void HoldsTheAnchorWhenTheClockIsBehindIt()
        {
            var buffer = Create(20, 200f);
            buffer.Add(21, new Tracked(210f));

            Assert.That(buffer.Sample(18.5d).value, Is.EqualTo(200f).Within(1e-4));
            Assert.That(buffer.Count, Is.EqualTo(1), "a sample ahead of the clock must stay buffered");
        }

        [Test]
        public void ReplacesASampleForTheSameTickAndDisposesTheOld()
        {
            var buffer = Create(10, 100f);
            var first = new Tracked(110f);
            var second = new Tracked(111f);
            buffer.Add(11, first);
            buffer.Add(11, second);

            Assert.That(first.disposed, Is.True);
            Assert.That(buffer.Count, Is.EqualTo(1));
            Assert.That(buffer.Sample(11d).value, Is.EqualTo(111f).Within(1e-4));
        }

        [Test]
        public void ASampleOlderThanTheAnchorRestartsTheViewFromIt()
        {
            var buffer = Create(10, 100f);
            var ahead = new Tracked(200f);
            buffer.Teleport(20, ahead);
            var pending = new Tracked(210f);
            buffer.Add(21, pending);

            var restarted = new Tracked(150f);
            buffer.Add(15, restarted);
            Assert.That(ahead.disposed, Is.True);
            Assert.That(pending.disposed, Is.True);
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(buffer.anchorTick, Is.EqualTo(15UL));
            Assert.That(buffer.Sample(15.5d).value, Is.EqualTo(150f).Within(1e-4));

            buffer.Add(16, new Tracked(160f));
            Assert.That(buffer.Sample(15.5d).value, Is.EqualTo(155f).Within(1e-4));
        }

        [Test]
        public void DropsTheOldestPendingSampleBeyondCapacity()
        {
            var buffer = Create(10, 100f, capacity: 2);
            var oldest = new Tracked(110f);
            buffer.Add(11, oldest);
            buffer.Add(12, new Tracked(120f));
            buffer.Add(13, new Tracked(130f));

            Assert.That(oldest.disposed, Is.True);
            Assert.That(buffer.Count, Is.EqualTo(2));
            Assert.That(buffer.Sample(10.5d).value, Is.EqualTo(105f).Within(1e-4),
                "the anchor still spans to the oldest surviving sample");
        }

        [Test]
        public void TeleportClearsPendingSamplesAndRestartsFromTheGivenTick()
        {
            var buffer = Create(10, 100f);
            var pending = new Tracked(110f);
            buffer.Add(11, pending);

            buffer.Teleport(30, new Tracked(300f));
            Assert.That(pending.disposed, Is.True);
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(buffer.Sample(12d).value, Is.EqualTo(300f).Within(1e-4), "held until the clock reaches the teleport");

            buffer.Add(31, new Tracked(310f));
            Assert.That(buffer.Sample(30.5d).value, Is.EqualTo(305f).Within(1e-4));
        }

        [Test]
        public void CommittingDisposesTheReplacedAnchor()
        {
            var initial = new Tracked(100f);
            var buffer = new PredictedViewBuffer<Tracked>(Lerp, 10, initial, 8);
            buffer.Add(11, new Tracked(110f));
            buffer.Sample(11d);
            Assert.That(initial.disposed, Is.True);
        }

        [Test]
        public void IdentitiesSpawnedAtDifferentTimesPresentTheSameTick()
        {
            var early = Create(10, 100f);
            for (ulong tick = 11; tick <= 14; tick++)
                early.Add(tick, new Tracked(tick * 10f));

            var late = Create(12, 120f);
            late.Add(13, new Tracked(130f));
            late.Add(14, new Tracked(140f));

            foreach (var present in new[] { 12.3d, 12.9d, 13.4d })
            {
                Assert.That(early.Sample(present).value, Is.EqualTo(present * 10d).Within(1e-3), $"early at {present}");
                Assert.That(late.Sample(present).value, Is.EqualTo(present * 10d).Within(1e-3), $"late at {present}");
            }
        }

        [Test]
        public void CountAheadDoesNotCommit()
        {
            var buffer = Create(10, 100f);
            buffer.Add(11, new Tracked(110f));
            buffer.Add(12, new Tracked(120f));

            Assert.That(buffer.CountAhead(11.5d), Is.EqualTo(1));
            Assert.That(buffer.anchorTick, Is.EqualTo(10UL));
            Assert.That(buffer.Count, Is.EqualTo(2));
        }
    }
}
