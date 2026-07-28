using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class RenderPhaseFrameApplyTests
    {
        [Test]
        public void RenderPhaseApplyRequiresQueuedFrames()
        {
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 0, false, false), Is.False);
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 1, false, false), Is.True);
        }

        [Test]
        public void RenderPhaseApplyWaitsForTheFirstSimulatedTick()
        {
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(1, 1, false, false), Is.False);
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 1, false, false), Is.True);
        }

        [Test]
        public void RenderPhaseApplyIsBlockedWhileSimulatingOrReplaying()
        {
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 1, true, false), Is.False);
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 1, false, true), Is.False);
            Assert.That(PredictionManager.ShouldApplyQueuedFramesInRenderPhase(2, 1, true, true), Is.False);
        }

        [Test]
        public void ViewLatchAlwaysReplacedOutsideRefreshMode()
        {
            Assert.That(PredictionManager.ShouldReplaceViewLatch(false, false), Is.True);
            Assert.That(PredictionManager.ShouldReplaceViewLatch(false, true), Is.True);
        }

        [Test]
        public void ViewLatchOnlyRefreshedWhenAPendingSampleExists()
        {
            Assert.That(PredictionManager.ShouldReplaceViewLatch(true, true), Is.True);
            Assert.That(PredictionManager.ShouldReplaceViewLatch(true, false), Is.False);
        }
    }
}
