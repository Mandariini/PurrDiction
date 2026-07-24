using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Selects which predicted hierarchy roots are replicated to a player.
    /// The prediction manager always includes its built-in systems and identities owned by
    /// the receiver. Returning true includes the complete spawned root, including all pieces.
    /// </summary>
    public interface IPredictionVisibilityProvider
    {
        bool CanSee(PlayerID player, PredictedObjectID rootId, GameObject root);
    }

    internal readonly struct PredictionVisibilityTransition
    {
        public readonly ulong tick;
        public readonly bool visible;

        public PredictionVisibilityTransition(ulong tick, bool visible)
        {
            this.tick = tick;
            this.visible = visible;
        }
    }

    /// <summary>
    /// Per-receiver visibility timeline. It stores transitions rather than a complete object
    /// set for every frame, while still allowing any acknowledged frame to be used as a
    /// replication baseline.
    /// </summary>
    internal sealed class PlayerVisibilityTimeline
    {
        readonly HashSet<PredictedObjectID> _current = new ();
        readonly Dictionary<PredictedObjectID, List<PredictionVisibilityTransition>> _transitions = new ();
        readonly List<PredictedObjectID> _removedScratch = new ();

        public IReadOnlyCollection<PredictedObjectID> current => _current;
        internal int trackedRootCount => _transitions.Count;
        public ulong latestRecordedTick { get; private set; }
        public ulong latestTransitionTick { get; private set; }
        public uint revision { get; private set; }

        public bool IsVisible(PredictedObjectID rootId)
        {
            return _current.Contains(rootId);
        }

        public void Record(ulong tick, HashSet<PredictedObjectID> desired)
        {
            _removedScratch.Clear();

            foreach (var rootId in _current)
            {
                if (!desired.Contains(rootId))
                    _removedScratch.Add(rootId);
            }

            for (var i = 0; i < _removedScratch.Count; i++)
            {
                var rootId = _removedScratch[i];
                _current.Remove(rootId);
                AddTransition(rootId, tick, false);
            }

            foreach (var rootId in desired)
            {
                if (_current.Add(rootId))
                    AddTransition(rootId, tick, true);
            }

            if (tick > latestRecordedTick)
                latestRecordedTick = tick;
        }

        public bool WasVisibleAt(PredictedObjectID rootId, ulong tick)
        {
            if (!_transitions.TryGetValue(rootId, out var transitions) || transitions.Count == 0)
                return false;

            int low = 0;
            int high = transitions.Count - 1;
            int found = -1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (transitions[mid].tick <= tick)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return found >= 0 && transitions[found].visible;
        }

        /// <summary>
        /// True only when the root is currently visible and has not left/re-entered since
        /// the supplied baseline. A re-entry therefore remains a full-state entry until a
        /// frame from the new visibility generation is acknowledged.
        /// </summary>
        public bool HasContinuousVisibilityFrom(PredictedObjectID rootId, ulong baselineTick)
        {
            if (!_current.Contains(rootId) ||
                !_transitions.TryGetValue(rootId, out var transitions) ||
                transitions.Count == 0)
                return false;

            var latest = transitions[^1];
            return latest.visible && latest.tick <= baselineTick;
        }

        public bool HasTransitionAfter(ulong baselineTick)
        {
            return latestTransitionTick > baselineTick;
        }

        /// <summary>
        /// Acknowledgements only move forward. Collapse older transitions to one anchor so
        /// long-lived sessions retain memory proportional to visibility churn after the
        /// latest usable baseline.
        /// </summary>
        public void PruneThrough(ulong acknowledgedTick)
        {
            foreach (var transitions in _transitions.Values)
            {
                int anchor = -1;
                for (var i = 0; i < transitions.Count; i++)
                {
                    if (transitions[i].tick > acknowledgedTick)
                        break;
                    anchor = i;
                }

                if (anchor > 0)
                    transitions.RemoveRange(0, anchor);
            }

            if (_current.Count == _transitions.Count)
                return;

            _removedScratch.Clear();
            foreach (var pair in _transitions)
            {
                var transitions = pair.Value;
                if (transitions.Count == 1 &&
                    !transitions[0].visible &&
                    transitions[0].tick <= acknowledgedTick &&
                    !_current.Contains(pair.Key))
                {
                    _removedScratch.Add(pair.Key);
                }
            }

            for (var i = 0; i < _removedScratch.Count; i++)
                _transitions.Remove(_removedScratch[i]);
        }

        public void Clear()
        {
            _current.Clear();
            _transitions.Clear();
            _removedScratch.Clear();
            latestRecordedTick = 0;
            latestTransitionTick = 0;
            revision = 0;
        }

        void AddTransition(PredictedObjectID rootId, ulong tick, bool visible)
        {
            if (!_transitions.TryGetValue(rootId, out var transitions))
            {
                transitions = new List<PredictionVisibilityTransition>(4);
                _transitions.Add(rootId, transitions);
            }

            if (transitions.Count > 0 && transitions[^1].tick == tick)
            {
                transitions[^1] = new PredictionVisibilityTransition(tick, visible);
            }
            else
            {
                transitions.Add(new PredictionVisibilityTransition(tick, visible));
            }

            latestTransitionTick = tick;
            revision++;
        }
    }
}
