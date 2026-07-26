using System.Collections.Generic;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionVisibilityHiddenSetTests
    {
        [Test]
        public void HiddenRootIsCollectedFromItsHideTickOnward()
        {
            var timeline = new PlayerVisibilityTimeline();
            var root = new PredictedObjectID(700);
            timeline.SetVisible(10, root, false);

            var result = new HashSet<PredictedObjectID>();
            timeline.CollectHiddenRootsAt(9, result);
            Assert.That(result, Is.Empty,
                "a root must not be collected before its hide tick");

            timeline.CollectHiddenRootsAt(10, result);
            Assert.That(result, Is.EquivalentTo(new[] { root }));

            result.Clear();
            timeline.CollectHiddenRootsAt(50, result);
            Assert.That(result, Is.EquivalentTo(new[] { root }));
        }

        [Test]
        public void HiddenThenShownRootIsCollectedOnlyInsideTheHiddenWindow()
        {
            var timeline = new PlayerVisibilityTimeline();
            var root = new PredictedObjectID(710);
            timeline.SetVisible(10, root, false);
            timeline.SetVisible(15, root, true);

            var result = new HashSet<PredictedObjectID>();
            timeline.CollectHiddenRootsAt(9, result);
            Assert.That(result, Is.Empty);

            timeline.CollectHiddenRootsAt(12, result);
            Assert.That(result, Is.EquivalentTo(new[] { root }),
                "a history-only root must still be collected inside its hidden window");

            result.Clear();
            timeline.CollectHiddenRootsAt(15, result);
            Assert.That(result, Is.Empty,
                "the re-show tick ends the hidden window");

            timeline.CollectHiddenRootsAt(20, result);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void AlwaysVisibleRootIsNeverCollected()
        {
            var timeline = new PlayerVisibilityTimeline();
            var hidden = new PredictedObjectID(720);
            var untouched = new PredictedObjectID(721);
            timeline.SetVisible(10, hidden, false);

            var result = new HashSet<PredictedObjectID>();
            for (ulong tick = 0; tick <= 20; tick++)
            {
                result.Clear();
                timeline.CollectHiddenRootsAt(tick, result);
                Assert.That(result.Contains(untouched), Is.False);
            }
        }

        [Test]
        public void DefaultHiddenTimelineContributesNoHiddenRoots()
        {
            var timeline = new PlayerVisibilityTimeline(defaultVisible: false);
            var root = new PredictedObjectID(730);
            timeline.SetVisible(10, root, true);

            var result = new HashSet<PredictedObjectID>();
            timeline.CollectHiddenRootsAt(5, result);
            timeline.CollectHiddenRootsAt(15, result);
            Assert.That(result, Is.Empty);
        }
    }
}
