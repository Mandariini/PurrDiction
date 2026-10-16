using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Prediction.Profiler;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class TickBandwidthCollectionTests
    {
        static readonly FieldInfo Subscribers = typeof(TickBandwidthProfiler).GetField("onTickEnded",
            BindingFlags.Static | BindingFlags.NonPublic);
        Action _previousSubscribers;
        bool _previousPolling;

        [SetUp]
        public void IsolateConsumers()
        {
            _previousSubscribers = (Action)Subscribers.GetValue(null);
            _previousPolling = TickBandwidthProfiler.collectWithoutSubscribers;
            Subscribers.SetValue(null, null);
            TickBandwidthProfiler.collectWithoutSubscribers = false;
            TickBandwidthProfiler.MarkEndOfTick();
        }

        [TearDown]
        public void RestoreConsumers()
        {
            Subscribers.SetValue(null, null);
            TickBandwidthProfiler.MarkEndOfTick();
            TickBandwidthProfiler.collectWithoutSubscribers = _previousPolling;
            Subscribers.SetValue(null, _previousSubscribers);
        }

        [Test]
        public void UnobservedTicksDoNotRecordStateOrInputSamples()
        {
            RecordAll();
            Assert.That(TickBandwidthProfiler.wroteStates, Is.Empty);
            Assert.That(TickBandwidthProfiler.readStates, Is.Empty);
            Assert.That(TickBandwidthProfiler.wroteInputs, Is.Empty);
            Assert.That(TickBandwidthProfiler.readInputs, Is.Empty);
        }

        [Test]
        public void TickSubscribersReceiveAllSamplesAndUnsubscribingStopsCapture()
        {
            IReadOnlyList<PackingInfo> observed = null;
            int ticks = 0;
            Action consumer = () =>
            {
                ticks++;
                observed = TickBandwidthProfiler.wroteStates;
                Assert.That(observed.Count, Is.EqualTo(1));
                Assert.That(observed[0].parent, Is.EqualTo(typeof(TickBandwidthCollectionTests)));
                Assert.That(observed[0].bitCount, Is.EqualTo(11));
                Assert.That(TickBandwidthProfiler.readStates[0].bitCount, Is.EqualTo(12));
                Assert.That(TickBandwidthProfiler.wroteInputs[0].bitCount, Is.EqualTo(13));
                Assert.That(TickBandwidthProfiler.readInputs[0].bitCount, Is.EqualTo(14));
            };
            TickBandwidthProfiler.onTickEnded += consumer;
            RecordAll();
            TickBandwidthProfiler.MarkEndOfTick();
            Assert.That(ticks, Is.EqualTo(1));
            Assert.That(observed, Is.Empty, "samples are only live during the tick-ended callback");
            Assert.That(TickBandwidthProfiler.collectWithoutSubscribers, Is.False,
                "reading inside the profiler callback must not permanently enable collection");

            TickBandwidthProfiler.onTickEnded -= consumer;
            RecordAll();
            Assert.That(observed, Is.Empty);
        }

        [Test]
        public void DirectPollingStartsCaptureAndCanBeStopped()
        {
            var observed = TickBandwidthProfiler.wroteStates;
            Assert.That(TickBandwidthProfiler.collectWithoutSubscribers, Is.True);
            TickBandwidthProfiler.OnWroteState(typeof(int), 17, null);
            Assert.That(observed.Count, Is.EqualTo(1));
            Assert.That(observed[0].bitCount, Is.EqualTo(17));
            TickBandwidthProfiler.collectWithoutSubscribers = false;
            TickBandwidthProfiler.OnWroteState(typeof(int), 19, null);
            Assert.That(observed.Count, Is.EqualTo(1));
            TickBandwidthProfiler.MarkEndOfTick();
            Assert.That(observed, Is.Empty);
        }

        [Test]
        public void AFailingConsumerStillClearsTheFinishedTick()
        {
            IReadOnlyList<PackingInfo> observed = null;
            TickBandwidthProfiler.onTickEnded += () =>
            {
                observed = TickBandwidthProfiler.wroteStates;
                throw new InvalidOperationException("profiler consumer failed");
            };
            RecordAll();
            Assert.Throws<InvalidOperationException>(TickBandwidthProfiler.MarkEndOfTick);
            Assert.That(observed, Is.Empty);
            Assert.That(TickBandwidthProfiler.collectWithoutSubscribers, Is.False);
        }

        static void RecordAll()
        {
            var type = typeof(TickBandwidthCollectionTests);
            TickBandwidthProfiler.OnWroteState(type, 11, null);
            TickBandwidthProfiler.OnReadState(type, 12, null);
            TickBandwidthProfiler.OnWroteInput(type, 13, null);
            TickBandwidthProfiler.OnReadInput(type, 14, null);
        }
    }
}
