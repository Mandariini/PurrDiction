using System;
using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        static readonly ProfilerMarker PreparePlayerVisibilityMarker =
            new("PredictionManager.PreparePlayerVisibility");
        static readonly ProfilerMarker CommitVisibilityChangesMarker =
            new("PredictionManager.CommitVisibilityChanges");
        static readonly ProfilerMarker BuildVisibilityHierarchyProjectionMarker =
            new("PredictionManager.BuildVisibilityHierarchyProjection");

        readonly Dictionary<PlayerID, PlayerVisibilityTimeline> _playerVisibility = new ();
        readonly Dictionary<PlayerID, HashSet<PredictedObjectID>> _hiddenVisibility = new ();
        readonly Dictionary<PlayerID, Dictionary<PredictedObjectID, int>> _visibilityAcquisitions = new ();
        readonly Dictionary<PlayerID, HashSet<PredictedObjectID>> _dirtyVisibility = new ();
        readonly Dictionary<PlayerID, Dictionary<PredictedObjectID, ulong>> _hiddenVisibilityAckCandidates = new ();
        readonly Dictionary<PlayerID, HashSet<PredictedObjectID>> _retiredVisibilityRoots = new ();
        readonly Dictionary<ulong, VisibilityAcquisition> _visibilityAcquisitionTokens = new ();
        readonly List<ulong> _visibilityTokenScratch = new ();
        readonly HashSet<PlayerID> _visibilityDependencyRebuild = new ();
        readonly HashSet<PredictedObjectID> _desiredVisibilityScratch = new ();
        readonly HashSet<PredictedObjectID> _hiddenDependencyScratch = new ();
        readonly HashSet<PredictedObjectID> _spawnedRootScratch = new ();
        readonly HashSet<PredictedObjectID> _ownedRootScratch = new ();
        readonly List<PredictedIdentity> _addressedSystemScratch = new ();
        readonly List<PredictedIdentity> _addressedReadSystemScratch = new ();
        readonly HashSet<PredictedComponentID> _addressedReadIdsScratch = new ();
        readonly List<PredictedObjectID> _visibilityRootScratch = new ();

        ulong _nextVisibilityAcquisitionToken = 1;

        /// <summary>
        /// Hides one whole predicted hierarchy root and its attached descendant roots from a
        /// player. Passing a piece id resolves it to its root while that piece exists. An owned
        /// or acquired descendant remains visible and promotes only the ancestors it requires.
        /// </summary>
        public bool HideFrom(PlayerID player, PredictedObjectID objectId)
        {
            objectId = ResolveVisibilityRoot(objectId);
            if (!_hiddenVisibility.TryGetValue(player, out var hidden))
            {
                hidden = new HashSet<PredictedObjectID>();
                _hiddenVisibility.Add(player, hidden);
            }

            if (!hidden.Add(objectId))
                return false;

            MarkVisibilityDirty(player, objectId);
            return true;
        }

        /// <summary>
        /// Restores the default-visible policy for one whole predicted hierarchy root.
        /// Active visibility acquisitions are unaffected.
        /// </summary>
        public bool ShowTo(PlayerID player, PredictedObjectID objectId)
        {
            objectId = ResolveVisibilityRoot(objectId);
            if (!_hiddenVisibility.TryGetValue(player, out var hidden) ||
                !hidden.Remove(objectId))
            {
                return false;
            }

            if (hidden.Count == 0)
                _hiddenVisibility.Remove(player);
            MarkVisibilityDirty(player, objectId);
            return true;
        }

        /// <summary>
        /// Clears the explicit hidden state and returns the root to the default-visible policy.
        /// </summary>
        public bool ResetVisibility(PlayerID player, PredictedObjectID objectId)
        {
            return ShowTo(player, objectId);
        }

        /// <summary>
        /// Temporarily forces one whole predicted hierarchy root visible to a player, together
        /// with any attached ancestors required to represent it. Acquisitions overlap safely:
        /// the underlying hidden policy becomes effective only after the final handle is disposed.
        /// </summary>
        public IDisposable AcquireVisibility(
            PlayerID player,
            PredictedObjectID objectId)
        {
            objectId = ResolveVisibilityRoot(objectId);

            if (!_visibilityAcquisitions.TryGetValue(player, out var acquisitions))
            {
                acquisitions = new Dictionary<PredictedObjectID, int>();
                _visibilityAcquisitions.Add(player, acquisitions);
            }

            acquisitions.TryGetValue(objectId, out int count);
            if (count == int.MaxValue)
                throw new InvalidOperationException("Too many overlapping visibility acquisitions.");

            acquisitions[objectId] = count + 1;
            if (count == 0)
                MarkVisibilityDirty(player, objectId);

            ulong token = AllocateVisibilityAcquisitionToken();
            _visibilityAcquisitionTokens.Add(
                token,
                new VisibilityAcquisition(player, objectId));
            return new VisibilityAcquisitionHandle(this, token);
        }

        PredictedObjectID ResolveVisibilityRoot(PredictedObjectID objectId)
        {
            if (hierarchy && hierarchy.TryGetRootId(objectId, out var rootId))
                return rootId;
            return objectId;
        }

        internal PlayerVisibilityTimeline PreparePlayerVisibility(
            PlayerID player,
            ulong tick,
            ulong acknowledgedTick)
        {
            using var prepareVisibilityScope = PreparePlayerVisibilityMarker.Auto();

            if (!_playerVisibility.TryGetValue(player, out var timeline))
            {
                timeline = new PlayerVisibilityTimeline(defaultVisible: true);
                _playerVisibility.Add(player, timeline);
            }

            AcknowledgeHiddenVisibility(player, timeline, acknowledgedTick);
            AcknowledgePendingVisibilityDeletes(player, timeline, acknowledgedTick);

            using (CommitVisibilityChangesMarker.Auto())
                CommitVisibilityChanges(player, timeline, tick);
            timeline.PruneThrough(acknowledgedTick);

            return timeline;
        }

        bool CommitVisibilityChanges(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            bool rebuildDependencies = _visibilityDependencyRebuild.Remove(player);
            if (!_dirtyVisibility.TryGetValue(player, out var dirty) ||
                dirty.Count == 0)
            {
                return rebuildDependencies &&
                       RebuildPlayerVisibility(player, timeline, tick);
            }

            bool changed;
            if (rebuildDependencies ||
                hierarchy && hierarchy.hasCrossRootVisibilityDependencies)
            {
                changed = RebuildPlayerVisibility(player, timeline, tick);

                foreach (var rootId in dirty)
                {
                    if (_spawnedRootScratch.Contains(rootId))
                        continue;

                    changed |= SetTimelineVisibility(
                        player,
                        timeline,
                        tick,
                        rootId,
                        IsVisibilityRequested(player, rootId));
                }
            }
            else
            {
                changed = false;
                CollectOwnedVisibilityRoots(player);
                foreach (var rootId in dirty)
                {
                    changed |= SetTimelineVisibility(
                        player,
                        timeline,
                        tick,
                        rootId,
                        IsVisibilityRequested(player, rootId));
                }
            }

            dirty.Clear();
            _dirtyVisibility.Remove(player);
            return changed;
        }

        bool RebuildPlayerVisibility(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            _spawnedRootScratch.Clear();
            _desiredVisibilityScratch.Clear();
            _hiddenDependencyScratch.Clear();
            _ownedRootScratch.Clear();

            if (!hierarchy)
                return false;

            hierarchy.CollectSpawnedRoots(_spawnedRootScratch);
            CollectOwnedVisibilityRoots(player);

            if (_hiddenVisibility.TryGetValue(player, out var hidden))
            {
                foreach (var rootId in hidden)
                    _hiddenDependencyScratch.Add(rootId);
                hierarchy.ExpandVisibilityDependents(_hiddenDependencyScratch);
            }

            foreach (var rootId in _spawnedRootScratch)
            {
                if (_ownedRootScratch.Contains(rootId) ||
                    IsVisibilityAcquired(player, rootId) ||
                    !_hiddenDependencyScratch.Contains(rootId))
                {
                    _desiredVisibilityScratch.Add(rootId);
                }
            }

            hierarchy.ExpandVisibilityDependencies(_desiredVisibilityScratch);

            bool changed = false;
            foreach (var rootId in _spawnedRootScratch)
            {
                changed |= SetTimelineVisibility(
                    player,
                    timeline,
                    tick,
                    rootId,
                    _desiredVisibilityScratch.Contains(rootId));
            }

            // Descendant hides are derived from the current attachment graph. Once a root
            // leaves that graph, re-evaluate its own persistent policy and record any
            // transition so unacknowledged frames retain correct history.
            _desiredVisibilityScratch.Clear();
            timeline.CollectCurrentExceptions(_desiredVisibilityScratch);
            foreach (var rootId in _desiredVisibilityScratch)
            {
                if (_spawnedRootScratch.Contains(rootId))
                    continue;

                changed |= SetTimelineVisibility(
                    player,
                    timeline,
                    tick,
                    rootId,
                    IsVisibilityRequested(player, rootId));
            }

            return changed;
        }

        bool SetTimelineVisibility(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick,
            PredictedObjectID rootId,
            bool visible)
        {
            bool wasVisible = timeline.IsVisible(rootId);
            bool changed = timeline.SetVisible(tick, rootId, visible);
            if (wasVisible != visible)
            {
                TrackVisibilityTransition(
                    player,
                    timeline,
                    rootId,
                    visible,
                    tick);
            }
            return changed;
        }

        void TrackVisibilityTransition(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            PredictedObjectID rootId,
            bool visible,
            ulong tick)
        {
            if (visible ||
                !TryGetPlayerVisibilityFrame(player, out var frame) ||
                GetVisibilityProjectionStatus(
                    timeline,
                    rootId,
                    frame.sentVisibilityTick) == VisibilityProjectionStatus.Absent)
            {
                return;
            }

            if (!_hiddenVisibilityAckCandidates.TryGetValue(player, out var candidates))
            {
                candidates = new Dictionary<PredictedObjectID, ulong>();
                _hiddenVisibilityAckCandidates.Add(player, candidates);
            }

            candidates[rootId] = tick;
        }

        internal bool HasSentVisibilityRoot(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            PredictedObjectID rootId,
            ulong acknowledgedTick)
        {
            if (!TryGetPlayerVisibilityFrame(player, out var frame))
                return false;

            return HasSentVisibilityRoot(
                player,
                timeline,
                rootId,
                acknowledgedTick,
                frame);
        }

        bool HasSentVisibilityRoot(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            PredictedObjectID rootId,
            ulong acknowledgedTick,
            in PlayerPacker frame)
        {
            AcknowledgeHiddenVisibility(player, timeline, acknowledgedTick);
            var prepared = GetVisibilityProjectionStatus(
                timeline,
                rootId,
                frame.preparedVisibilityTick);
            if (prepared != VisibilityProjectionStatus.Absent)
                return true;

            if (_retiredVisibilityRoots.TryGetValue(player, out var retired) &&
                retired.Contains(rootId))
            {
                return false;
            }

            return GetVisibilityProjectionStatus(
                       timeline,
                       rootId,
                       frame.sentVisibilityTick) != VisibilityProjectionStatus.Absent;
        }

        bool TryGetPlayerVisibilityFrame(
            PlayerID player,
            out PlayerPacker frame)
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                frame = _clientFrames[i];
                if (frame.player.Equals(player))
                    return true;
            }

            frame = default;
            return false;
        }

        VisibilityProjectionStatus GetVisibilityProjectionStatus(
            PlayerVisibilityTimeline timeline,
            PredictedObjectID rootId,
            ulong tick)
        {
            if (tick == 0 || !timeline.WasVisibleAt(rootId, tick))
                return VisibilityProjectionStatus.Absent;

            if (!hierarchy)
                return VisibilityProjectionStatus.Absent;

            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState state))
            {
                // A receiver can suppress new frames while a reliable frame is in flight.
                // If that lasts beyond verified-history retention, preserve correctness by
                // allowing a redundant tombstone rather than risking a missed delete.
                return VisibilityProjectionStatus.Unknown;
            }

            return PredictedHierarchy.StateContainsRoot(state, rootId)
                ? VisibilityProjectionStatus.Present
                : VisibilityProjectionStatus.Absent;
        }

        internal void HandleVisibilityFrameSent(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            if (_hiddenVisibilityAckCandidates.TryGetValue(player, out var hidden))
            {
                _visibilityRootScratch.Clear();
                foreach (var pair in hidden)
                {
                    if (GetVisibilityProjectionStatus(
                            timeline,
                            pair.Key,
                            tick) == VisibilityProjectionStatus.Present)
                    {
                        _visibilityRootScratch.Add(pair.Key);
                    }
                }

                for (var i = 0; i < _visibilityRootScratch.Count; i++)
                    hidden.Remove(_visibilityRootScratch[i]);
                _visibilityRootScratch.Clear();

                if (hidden.Count == 0)
                    _hiddenVisibilityAckCandidates.Remove(player);
            }

            if (!_retiredVisibilityRoots.TryGetValue(player, out var retired))
                return;

            _visibilityRootScratch.Clear();
            foreach (var rootId in retired)
            {
                if (GetVisibilityProjectionStatus(timeline, rootId, tick) !=
                    VisibilityProjectionStatus.Unknown)
                {
                    _visibilityRootScratch.Add(rootId);
                }
            }

            for (var i = 0; i < _visibilityRootScratch.Count; i++)
                retired.Remove(_visibilityRootScratch[i]);
            _visibilityRootScratch.Clear();

            if (retired.Count == 0)
                _retiredVisibilityRoots.Remove(player);
        }

        void AcknowledgeHiddenVisibility(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong acknowledgedTick)
        {
            if (!_hiddenVisibilityAckCandidates.TryGetValue(player, out var candidates) ||
                candidates.Count == 0)
            {
                return;
            }

            _visibilityRootScratch.Clear();
            foreach (var pair in candidates)
            {
                if (pair.Value <= acknowledgedTick &&
                    !timeline.WasVisibleAt(pair.Key, acknowledgedTick))
                {
                    _visibilityRootScratch.Add(pair.Key);
                }
            }

            for (var i = 0; i < _visibilityRootScratch.Count; i++)
            {
                var rootId = _visibilityRootScratch[i];
                candidates.Remove(rootId);
                RetireVisibilityRoot(player, rootId);
            }
            _visibilityRootScratch.Clear();

            if (candidates.Count == 0)
                _hiddenVisibilityAckCandidates.Remove(player);
        }

        internal void RetireVisibilityRoot(
            PlayerID player,
            PredictedObjectID rootId)
        {
            if (!_retiredVisibilityRoots.TryGetValue(player, out var retired))
            {
                retired = new HashSet<PredictedObjectID>();
                _retiredVisibilityRoots.Add(player, retired);
            }

            retired.Add(rootId);
        }

        enum VisibilityProjectionStatus : byte
        {
            Absent,
            Present,
            Unknown
        }
        bool IsVisibilityRequested(PlayerID player, PredictedObjectID rootId)
        {
            if (_ownedRootScratch.Contains(rootId) ||
                IsVisibilityAcquired(player, rootId))
            {
                return true;
            }

            return !_hiddenVisibility.TryGetValue(player, out var hidden) ||
                   !hidden.Contains(rootId);
        }

        bool IsVisibilityAcquired(PlayerID player, PredictedObjectID rootId)
        {
            return _visibilityAcquisitions.TryGetValue(player, out var acquisitions) &&
                   acquisitions.TryGetValue(rootId, out int count) &&
                   count > 0;
        }

        void CollectOwnedVisibilityRoots(PlayerID player)
        {
            _ownedRootScratch.Clear();
            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system ||
                    !system.owner.HasValue ||
                    system.owner.Value != player ||
                    system.id.objectId.instanceId.value == 1)
                {
                    continue;
                }

                _ownedRootScratch.Add(ResolveVisibilityRoot(system.id.objectId));
            }
        }

        void MarkVisibilityDirty(PlayerID player, PredictedObjectID rootId)
        {
            if (!_dirtyVisibility.TryGetValue(player, out var dirty))
            {
                dirty = new HashSet<PredictedObjectID>();
                _dirtyVisibility.Add(player, dirty);
            }

            dirty.Add(rootId);
        }


        void MarkVisibilityDirtyForOwnership(
            PlayerID player,
            PredictedObjectID rootId)
        {
            if (_playerVisibility.ContainsKey(player) ||
                _hiddenVisibility.ContainsKey(player) ||
                _visibilityAcquisitions.ContainsKey(player) ||
                _dirtyVisibility.ContainsKey(player))
            {
                MarkVisibilityDirty(player, rootId);
            }
        }

        internal void HandleVisibilityOwnershipChanged(
            PredictedIdentity system,
            PlayerID? previousOwner,
            PlayerID? currentOwner)
        {
            if (!cachedIsServer ||
                !system ||
                system.id.objectId.instanceId.value == 1 ||
                previousOwner.Equals(currentOwner))
            {
                return;
            }

            var rootId = ResolveVisibilityRoot(system.id.objectId);
            if (previousOwner.HasValue)
                MarkVisibilityDirtyForOwnership(previousOwner.Value, rootId);
            if (currentOwner.HasValue)
                MarkVisibilityDirtyForOwnership(currentOwner.Value, rootId);
        }

        internal void HandleVisibilitySystemRemoved(PredictedIdentity system)
        {
            if (!cachedIsServer ||
                !system ||
                !system.owner.HasValue ||
                system.id.objectId.instanceId.value == 1)
            {
                return;
            }

            MarkVisibilityDirtyForOwnership(
                system.owner.Value,
                ResolveVisibilityRoot(system.id.objectId));
        }

        internal void HandleVisibilityTopologyChanged()
        {
            if (!cachedIsServer)
                return;

            foreach (var player in _hiddenVisibility.Keys)
                _visibilityDependencyRebuild.Add(player);
            foreach (var player in _dirtyVisibility.Keys)
                _visibilityDependencyRebuild.Add(player);
            foreach (var player in _visibilityAcquisitions.Keys)
                _visibilityDependencyRebuild.Add(player);
        }

        ulong AllocateVisibilityAcquisitionToken()
        {
            ulong token;
            do
            {
                token = _nextVisibilityAcquisitionToken++;
                if (_nextVisibilityAcquisitionToken == 0)
                    _nextVisibilityAcquisitionToken = 1;
            } while (token == 0 || _visibilityAcquisitionTokens.ContainsKey(token));

            return token;
        }

        void ReleaseVisibility(ulong token)
        {
            if (!_visibilityAcquisitionTokens.Remove(token, out var acquisition) ||
                !_visibilityAcquisitions.TryGetValue(
                    acquisition.player,
                    out var acquisitions) ||
                !acquisitions.TryGetValue(acquisition.rootId, out int count))
            {
                return;
            }

            if (count > 1)
            {
                acquisitions[acquisition.rootId] = count - 1;
                return;
            }

            acquisitions.Remove(acquisition.rootId);
            if (acquisitions.Count == 0)
                _visibilityAcquisitions.Remove(acquisition.player);
            MarkVisibilityDirty(acquisition.player, acquisition.rootId);
        }

        void RemoveVisibilityAcquisitions(PlayerID player)
        {
            _visibilityAcquisitions.Remove(player);
            _visibilityTokenScratch.Clear();
            foreach (var pair in _visibilityAcquisitionTokens)
            {
                if (pair.Value.player == player)
                    _visibilityTokenScratch.Add(pair.Key);
            }

            for (var i = 0; i < _visibilityTokenScratch.Count; i++)
                _visibilityAcquisitionTokens.Remove(_visibilityTokenScratch[i]);
            _visibilityTokenScratch.Clear();
        }

        readonly struct VisibilityAcquisition
        {
            public readonly PlayerID player;
            public readonly PredictedObjectID rootId;

            public VisibilityAcquisition(
                PlayerID player,
                PredictedObjectID rootId)
            {
                this.player = player;
                this.rootId = rootId;
            }
        }

        sealed class VisibilityAcquisitionHandle : IDisposable
        {
            PredictionManager _manager;
            readonly ulong _token;

            public VisibilityAcquisitionHandle(
                PredictionManager manager,
                ulong token)
            {
                _manager = manager;
                _token = token;
            }

            public void Dispose()
            {
                var manager = _manager;
                _manager = null;
                if (!manager)
                    return;

                manager.ReleaseVisibility(_token);
            }
        }

        bool IsSystemVisible(
            PlayerVisibilityTimeline timeline,
            PredictedIdentity system)
        {
            if (!system || system.id.objectId.instanceId.value == 1 || !hierarchy)
                return true;

            var rootId = ResolveVisibilityRoot(system.id.objectId);
            return timeline.IsVisible(rootId);
        }

        bool WasSystemVisibleAt(
            PlayerVisibilityTimeline timeline,
            PredictedIdentity system,
            ulong tick)
        {
            if (!system || system.id.objectId.instanceId.value == 1 || !hierarchy)
                return true;

            var rootId = ResolveVisibilityRoot(system.id.objectId);
            return timeline.WasVisibleAt(rootId, tick);
        }

        bool RequiresFullEntryState(
            PlayerVisibilityTimeline timeline,
            PredictedIdentity system,
            ulong baselineTick,
            bool hasHierarchyBaseline,
            HashSet<PredictedObjectID> baselineRoots,
            HashSet<PredictedObjectID> baselinePieces)
        {
            if (system.id.objectId.instanceId.value == 1 || !hierarchy)
                return false;

            var rootId = ResolveVisibilityRoot(system.id.objectId);
            return !timeline.HasContinuousVisibilityFrom(rootId, baselineTick) ||
                   !hasHierarchyBaseline ||
                   !baselineRoots.Contains(rootId) ||
                   !baselinePieces.Contains(system.id.objectId);
        }

        sealed class HierarchyBaselineScratch
        {
            public ulong baselineTick = ulong.MaxValue;
            public ulong cachedAtLocalTick = ulong.MaxValue;
            public bool hasBaseline;
            public readonly HashSet<PredictedObjectID> roots = new();
            public readonly HashSet<PredictedObjectID> pieces = new();
        }

        readonly Dictionary<PlayerID, HierarchyBaselineScratch> _hierarchyBaselineScratchByPlayer = new();

        // WriteAddressedStateSection runs twice per player per tick (once for regular systems,
        // once more for event handlers via WriteEventHandles) with the same baselineTick both
        // times. Cache the hierarchy baseline root/piece scan per player so the second pass
        // reuses it instead of rescanning spawnedPrefabs from scratch. Scoped to the current
        // localTick only (not just a matching baselineTick): verified-history entries get
        // pruned over time, so if a player's ack stalls and baselineTick stops advancing, a
        // cache keyed on baselineTick alone would keep returning an increasingly stale answer
        // instead of noticing the entry it once found has since been pruned. Recomputing once
        // per tick costs nothing extra since neither pass runs more than once per localTick.
        HierarchyBaselineScratch GetHierarchyBaselineScratch(PlayerID player, ulong baselineTick)
        {
            if (!_hierarchyBaselineScratchByPlayer.TryGetValue(player, out var scratch))
            {
                scratch = new HierarchyBaselineScratch();
                _hierarchyBaselineScratchByPlayer[player] = scratch;
            }

            if (scratch.baselineTick == baselineTick && scratch.cachedAtLocalTick == localTick)
                return scratch;

            scratch.baselineTick = baselineTick;
            scratch.cachedAtLocalTick = localTick;
            scratch.roots.Clear();
            scratch.pieces.Clear();

            if (hierarchy &&
                hierarchy.TryGetVerifiedState(
                    baselineTick,
                    out _,
                    out PredictedHierarchyState hierarchyBaseline))
            {
                scratch.hasBaseline = true;
                PredictedHierarchy.CollectSpawnedRoots(hierarchyBaseline, scratch.roots);

                if (!hierarchyBaseline.spawnedPrefabs.isDisposed)
                {
                    for (var i = 0; i < hierarchyBaseline.spawnedPrefabs.Count; i++)
                        scratch.pieces.Add(hierarchyBaseline.spawnedPrefabs[i].instanceId);
                }
            }
            else
            {
                scratch.hasBaseline = false;
            }

            return scratch;
        }

        void WriteAddressedHierarchy(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            BitPacker destination,
            ulong tick,
            ulong baselineTick,
            bool fullFrame)
        {
            bool hasHierarchy = hierarchy;
            Packer<bool>.Write(destination, hasHierarchy);
            if (!hasHierarchy)
                return;

            hierarchy.RefreshVerifiedFromLive(tick);
            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentGlobal))
            {
                throw new InvalidOperationException(
                    $"No authoritative hierarchy state exists for tick {tick}.");
            }

            using var payload = BitPackerPool.Get();
            if (timeline.isPassThrough)
            {
                bool directWriteFull = fullFrame;
                if (!directWriteFull &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out var directBaselinePrediction,
                        out PredictedHierarchyState directBaselineGlobal))
                {
                    hierarchy.RunWriteVisibilityState(
                        player,
                        payload,
                        baselineTick,
                        directBaselinePrediction,
                        directBaselineGlobal,
                        currentGlobal);
                }
                else
                {
                    directWriteFull = true;
                    hierarchy.RunWriteFirstVisibilityState(
                        tick,
                        payload,
                        currentGlobal);
                }

                AddressedPredictionRecords.WriteRecord(
                    destination,
                    hierarchy.id,
                    directWriteFull,
                    payload);
                return;
            }

            PredictedHierarchyState currentProjection;
            using (BuildVisibilityHierarchyProjectionMarker.Auto())
            {
                currentProjection = PredictedHierarchy.BuildVisibilityProjection(
                    currentGlobal,
                    timeline,
                    tick);
            }

            bool writeFull = fullFrame;
            PredictedHierarchyState baselineProjection = default;

            try
            {
                if (!writeFull &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out var baselinePrediction,
                        out PredictedHierarchyState baselineGlobal))
                {
                    using (BuildVisibilityHierarchyProjectionMarker.Auto())
                    {
                        baselineProjection = PredictedHierarchy.BuildVisibilityProjection(
                            baselineGlobal,
                            timeline,
                            baselineTick);
                    }
                    hierarchy.RunWriteVisibilityState(
                        player,
                        payload,
                        baselineTick,
                        baselinePrediction,
                        baselineProjection,
                        currentProjection);
                }
                else
                {
                    writeFull = true;
                    hierarchy.RunWriteFirstVisibilityState(tick, payload, currentProjection);
                }

                AddressedPredictionRecords.WriteRecord(
                    destination,
                    hierarchy.id,
                    writeFull,
                    payload);
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }
        }

        void WriteAddressedStateSection(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            BitPacker destination,
            ulong tick,
            ulong baselineTick,
            bool fullFrame,
            bool eventHandlers)
        {
            _addressedSystemScratch.Clear();

            var baselineScratch = GetHierarchyBaselineScratch(player, baselineTick);

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system == hierarchy || system.isEventHandler != eventHandlers)
                    continue;
                if (!IsSystemVisible(timeline, system))
                    continue;

                _addressedSystemScratch.Add(system);
            }

            bool allowOmission = !fullFrame && baselineTick > 0;
            if (!allowOmission)
            {
                AddressedPredictionRecords.WriteSectionCount(
                    _addressedSystemScratch.Count,
                    destination);
            }

            using var records = allowOmission ? BitPackerPool.Get() : null;
            using var payload = BitPackerPool.Get();
            int writtenCount = 0;

            for (var i = 0; i < _addressedSystemScratch.Count; i++)
            {
                var system = _addressedSystemScratch[i];
                payload.ResetPositionAndMode(false);

                bool writeFull = fullFrame ||
                                 RequiresFullEntryState(
                                     timeline,
                                     system,
                                     baselineTick,
                                     baselineScratch.hasBaseline,
                                     baselineScratch.roots,
                                     baselineScratch.pieces);
                if (!writeFull && TryConsumeDesyncHeal(player, system.id))
                    writeFull = true;
                bool changed;

                if (!TryWriteAggregateVisibilityState(
                        system,
                        player,
                        timeline,
                        payload,
                        tick,
                        baselineTick,
                        ref writeFull,
                        out changed))
                {
                    if (writeFull)
                    {
                        system.RunWriteFirstState(tick, payload);
                        changed = true;
                    }
                    else
                    {
                        changed = system.RunWriteCurrentState(
                            player,
                            payload,
                            baselineTick);
                    }
                }

                bool canOmit = allowOmission &&
                               !writeFull &&
                               !changed &&
                               system.RunCanOmitUnchangedState(baselineTick);
                if (canOmit)
                    continue;

                AddressedPredictionRecords.WriteRecord(
                    allowOmission ? records : destination,
                    system.id,
                    writeFull,
                    payload);
                writtenCount++;
            }

            if (allowOmission)
            {
                AddressedPredictionRecords.WriteSectionCount(writtenCount, destination);
                destination.WriteBitsWithoutConsumingIt(records, records.positionInBits);
            }
        }
        void WriteAddressedFirstInputSection(
            PlayerVisibilityTimeline timeline,
            BitPacker destination,
            ulong tick)
        {
            _addressedSystemScratch.Clear();

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.hasInput || !IsSystemVisible(timeline, system))
                    continue;
                _addressedSystemScratch.Add(system);
            }

            Packer<PackedUInt>.Write(
                destination,
                (uint)_addressedSystemScratch.Count);

            using var payload = BitPackerPool.Get();
            for (var i = 0; i < _addressedSystemScratch.Count; i++)
            {
                var system = _addressedSystemScratch[i];
                payload.ResetPositionAndMode(false);
                system.WriteFirstInput(tick, payload);
                AddressedPredictionRecords.WriteRecord(
                    destination,
                    system.id,
                    true,
                    payload);
            }
        }

        struct CachedFilteredInputBlock
        {
            public ulong tick;
            public uint blockVersion;
            public uint timelineRevision;
            public BitPacker framed;
            public List<PredictedComponentID> ids;
        }

        sealed class PlayerInputWindowCache
        {
            public readonly CachedFilteredInputBlock[] slots =
                new CachedFilteredInputBlock[(int)MaxInputWindow + 1];
        }

        readonly Dictionary<PlayerID, PlayerInputWindowCache> _playerInputWindowCaches = new();
        readonly List<int> _visibleInputEntryScratch = new();

        readonly HashSet<PredictedComponentID> _filteredPrevIdScratch = new();

        // A culling receiver's filtered per-tick input record only changes when the shared block
        // for that tick is rebuilt (system list changed) or the receiver's visibility timeline
        // records a new transition. Both are versioned, so the filter-and-reframe pass runs once
        // per (player, tick) instead of once per tick of the redundancy window every server tick.
        // PruneThrough never changes WasVisibleAt answers for ticks past its anchor, and the
        // window only spans ticks past the receiver's acked tick, so pruning cannot stale a slot
        // that is still being served.
        // The cached form is the dedup form: it elides ids and payloads that this receiver
        // already decoded from its previous tick's block, so it is only valid for ticks that are
        // not the first of the receiver's window (the first tick is written inline in full form).
        // When the previous tick's slot is cold the block conservatively falls back to full
        // entries, which any receiver can decode.
        BitPacker GetFilteredInputBlockForTick(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong tick,
            in CachedInputBlock block)
        {
            if (!_playerInputWindowCaches.TryGetValue(player, out var cache))
            {
                cache = new PlayerInputWindowCache();
                _playerInputWindowCaches[player] = cache;
            }

            var index = (int)(tick % (ulong)cache.slots.Length);
            ref var slot = ref cache.slots[index];

            if (slot.framed != null &&
                slot.tick == tick &&
                slot.blockVersion == _inputBlockVersion &&
                slot.timelineRevision == timeline.revision)
            {
                return slot.framed;
            }

            var prevIndex = (int)((tick - 1) % (ulong)cache.slots.Length);
            ref var prevSlot = ref cache.slots[prevIndex];
            bool hasPrev = tick > 0 &&
                           prevSlot.framed != null &&
                           prevSlot.ids != null &&
                           prevSlot.tick == tick - 1 &&
                           prevSlot.blockVersion == _inputBlockVersion &&
                           prevSlot.timelineRevision == timeline.revision;

            slot.framed ??= BitPackerPool.Get();
            slot.ids ??= new List<PredictedComponentID>();
            slot.tick = tick;
            slot.blockVersion = _inputBlockVersion;
            slot.timelineRevision = timeline.revision;
            slot.ids.Clear();

            var framed = slot.framed;
            framed.ResetPositionAndMode(false);

            var entries = block.entries;
            _visibleInputEntryScratch.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                if (WasSystemVisibleAt(timeline, entries[i].system, tick))
                    _visibleInputEntryScratch.Add(i);
            }

            for (var i = 0; i < _visibleInputEntryScratch.Count; i++)
                slot.ids.Add(entries[_visibleInputEntryScratch[i]].system.id);

            bool sameIds = hasPrev && prevSlot.ids.Count == slot.ids.Count;
            if (sameIds)
            {
                for (var i = 0; i < slot.ids.Count; i++)
                {
                    if (!slot.ids[i].Equals(prevSlot.ids[i]))
                    {
                        sameIds = false;
                        break;
                    }
                }
            }

            bool prevIdSetBuilt = false;

            Packer<bool>.Write(framed, sameIds);
            if (!sameIds)
                Packer<PackedUInt>.Write(framed, (uint)_visibleInputEntryScratch.Count);

            for (var i = 0; i < _visibleInputEntryScratch.Count; i++)
            {
                var entry = entries[_visibleInputEntryScratch[i]];
                if (!sameIds)
                    Packer<PredictedComponentID>.Write(framed, entry.system.id);

                bool repeat = entry.samePayloadAsPrev && hasPrev;
                if (repeat && !sameIds)
                {
                    if (!prevIdSetBuilt)
                    {
                        _filteredPrevIdScratch.Clear();
                        for (var p = 0; p < prevSlot.ids.Count; p++)
                            _filteredPrevIdScratch.Add(prevSlot.ids[p]);
                        prevIdSetBuilt = true;
                    }

                    repeat = _filteredPrevIdScratch.Contains(entry.system.id);
                }

                Packer<bool>.Write(framed, repeat);
                if (repeat)
                    continue;

                Packer<PackedUInt>.Write(framed, (uint)entry.bitLength);
                framed.WriteBitDataWithoutConsumingIt(
                    new BitData(block.packer, entry.bitOrigin, entry.bitLength));
            }

            return framed;
        }

        // Self-contained full form of a receiver's filtered block, written straight into the
        // frame. Used for the first tick of the receiver's window, which anchors the dedup chain
        // and is served for at most one frame per tick, so caching it would never pay off.
        void WriteFilteredFullInputBlock(
            PlayerVisibilityTimeline timeline,
            ulong tick,
            in CachedInputBlock block,
            BitPacker frame)
        {
            var entries = block.entries;
            _visibleInputEntryScratch.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                if (WasSystemVisibleAt(timeline, entries[i].system, tick))
                    _visibleInputEntryScratch.Add(i);
            }

            Packer<bool>.Write(frame, false);
            Packer<PackedUInt>.Write(frame, (uint)_visibleInputEntryScratch.Count);
            for (var i = 0; i < _visibleInputEntryScratch.Count; i++)
            {
                var entry = entries[_visibleInputEntryScratch[i]];
                Packer<PredictedComponentID>.Write(frame, entry.system.id);
                Packer<bool>.Write(frame, false);
                Packer<PackedUInt>.Write(frame, (uint)entry.bitLength);
                frame.WriteBitDataWithoutConsumingIt(
                    new BitData(block.packer, entry.bitOrigin, entry.bitLength));
            }
        }

        void DisposePlayerInputWindowCache(PlayerID player)
        {
            if (!_playerInputWindowCaches.Remove(player, out var cache))
                return;

            for (var i = 0; i < cache.slots.Length; i++)
            {
                cache.slots[i].framed?.Dispose();
                cache.slots[i] = default;
            }
        }

        void DisposeAllPlayerInputWindowCaches()
        {
            foreach (var cache in _playerInputWindowCaches.Values)
            {
                for (var i = 0; i < cache.slots.Length; i++)
                {
                    cache.slots[i].framed?.Dispose();
                    cache.slots[i] = default;
                }
            }

            _playerInputWindowCaches.Clear();
        }

        void WriteVisibilityInputHistory(
            PlayerID player,
            BitPacker frame,
            ulong baselineTick,
            PlayerVisibilityTimeline timeline)
        {
            ulong from = baselineTick;
            if (localTick > MaxInputWindow && from < localTick - MaxInputWindow)
                from = localTick - MaxInputWindow;
            if (from > localTick)
                from = localTick;

            Packer<PackedUInt>.Write(frame, (uint)(localTick - from));

            // A receiver with no active/historical visibility exceptions sees everything, so its
            // per-tick record is identical to the shared cached block's own pre-framed contents -
            // blit it whole instead of re-filtering and re-framing every entry for this receiver.
            // The window's first tick always uses the self-contained full form; later ticks use
            // the dedup form, whose repeat markers reference the block the receiver just decoded.
            bool passThrough = timeline.isPassThrough;
            bool first = true;

            for (ulong tick = from + 1; tick <= localTick; tick++)
            {
                var block = GetInputBlockForTick(tick);

                if (passThrough)
                {
                    var blob = first ? block.framedPacker : block.dedupFramedPacker;
                    frame.WriteBitsWithoutConsumingIt(blob, blob.positionInBits);
                }
                else if (first)
                {
                    WriteFilteredFullInputBlock(timeline, tick, in block, frame);
                }
                else
                {
                    var filtered = GetFilteredInputBlockForTick(
                        player,
                        timeline,
                        tick,
                        block);
                    frame.WriteBitsWithoutConsumingIt(
                        filtered,
                        filtered.positionInBits);
                }

                first = false;
            }
        }

        readonly HashSet<PredictedComponentID> _recordDecodeQuarantine = new ();
        readonly Dictionary<PredictedComponentID, double> _recordFailureLogAt = new ();
        private bool _frameApplyHadRecordFailure;

        private const double RecordFailureLogIntervalSeconds = 5d;

        void OnAddressedRecordFailure(PredictedComponentID id, Exception error, int declaredBits, int consumedBits)
        {
            if (error is PredictedModuleRosterMismatchException)
            {
                if (_recordDecodeQuarantine.Add(id))
                {
                    PurrLogger.LogError(
                        $"{error.Message} State replication for this identity is suspended until it is destroyed or respawned.");
                }
                return;
            }

            _frameApplyHadRecordFailure = true;

            double now = Time.unscaledTimeAsDouble;
            if (_recordFailureLogAt.TryGetValue(id, out var lastLogged) &&
                now - lastLogged < RecordFailureLogIntervalSeconds)
                return;

            _recordFailureLogAt[id] = now;

            string identityName = _instanceMap.TryGetValue(id, out var failed) && failed
                ? failed.GetType().Name
                : "unresolved identity";

            PurrLogger.LogError(
                $"Discarded prediction record {id} ({identityName}); it consumed {consumedBits} of its " +
                $"declared {declaredBits} bits: {error.Message}\n" +
                "The rest of the frame was applied; the server will answer the stalled ack with a full sync.");
        }

        void ReadAddressedHierarchy(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool fullFrame,
            bool deferUnityApply)
        {
            bool hasHierarchy = default;
            Packer<bool>.Read(frame, ref hasHierarchy);
            if (!hasHierarchy)
                return;

            AddressedPredictionRecords.ReadOne(
                source: frame,
                readRecord: (id, isFullState, payload, _) =>
                {
                    if (!_instanceMap.TryGetValue(id, out var system) || system != hierarchy)
                    {
                        throw new InvalidOperationException(
                            $"Required hierarchy record {id} could not be resolved.");
                    }

                    ApplyAddressedState(
                        system,
                        payload,
                        fullFrame || isFullState,
                        stateTick,
                        baselineTick,
                        serverTick,
                        false,
                        !deferUnityApply);
                },
                onRecordFailure: OnAddressedRecordFailure);
        }

        void ReadAddressedStateSection(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool fullFrame,
            bool eventHandlers)
        {
            _addressedReadSystemScratch.Clear();
            _addressedReadIdsScratch.Clear();

            if (!fullFrame && baselineTick > 0)
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system &&
                        system != hierarchy &&
                        system.isEventHandler == eventHandlers &&
                        system.lastVerifiedTick.HasValue &&
                        system.lastVerifiedTick.Value >= baselineTick)
                    {
                        _addressedReadSystemScratch.Add(system);
                    }
                }
            }

            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, isFullState, payload, _) =>
                {
                    if (!_instanceMap.TryGetValue(id, out var system) ||
                        system.isEventHandler != eventHandlers ||
                        system == hierarchy)
                    {
                        return;
                    }

                    if (_recordDecodeQuarantine.Contains(id))
                        return;

                    if (!_addressedReadIdsScratch.Add(id))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate addressed state record {id}.");
                    }

                    ApplyAddressedState(
                        system,
                        payload,
                        fullFrame || isFullState,
                        stateTick,
                        baselineTick,
                        serverTick,
                        eventHandlers);
                },
                onRecordFailure: OnAddressedRecordFailure);

            if (fullFrame || baselineTick == 0)
                return;

            for (var i = 0; i < _addressedReadSystemScratch.Count; i++)
            {
                var system = _addressedReadSystemScratch[i];
                if (!system ||
                    _addressedReadIdsScratch.Contains(system.id) ||
                    _recordDecodeQuarantine.Contains(system.id) ||
                    !_instanceMap.TryGetValue(system.id, out var current) ||
                    current != system)
                {
                    continue;
                }

                try
                {
                    ApplyAddressedUnchangedState(
                        system,
                        stateTick,
                        baselineTick,
                        serverTick);
                }
                catch (Exception exception)
                {
                    OnAddressedRecordFailure(system.id, exception, 0, 0);
                }
            }

        }
        void ApplyAddressedUnchangedState(
            PredictedIdentity system,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (!system.RunCanReadUnchangedState(baselineTick))
            {
                throw new InvalidOperationException(
                    $"Missing acknowledged state baseline for omitted record {system.id} " +
                    $"at tick {baselineTick}.");
            }

            bool softCorrected = system.UsesSoftCorrectionTimeline();
            if (!softCorrected)
                system.RunClearFuture(stateTick);

            system.RunReadUnchangedState(
                stateTick,
                baselineTick,
                serverTick);

            if (!softCorrected)
                system.RunRollback(stateTick);
            system.lastVerifiedTick = stateTick;
        }

        void ReadAddressedFirstInputSection(BitPacker frame, ulong inputTick)
        {
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, _, payload, _) =>
                {
                    if (_instanceMap.TryGetValue(id, out var system) && system.hasInput)
                        system.ReadFirstInput(inputTick, payload);
                },
                onRecordFailure: OnAddressedRecordFailure);
        }

        void ApplyAddressedState(
            PredictedIdentity system,
            BitPacker payload,
            bool fullState,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool eventHandler,
            bool applyUnityState = true)
        {
            if (fullState)
            {
                system.RunClearFuture(stateTick);
                system.RunReadFirstState(stateTick, payload, serverTick);
                if (applyUnityState)
                {
                    if (ReferenceEquals(system, hierarchy))
                        ApplyPendingRemoteVisibilityDeletes(stateTick);
                    system.RunRollback(stateTick);
                    if (hierarchy && hierarchy.WasMaterializedByVerifiedApply(system.id.objectId))
                        system.RunResetInterpolation();
                    system.lastVerifiedTick = stateTick;
                }
                return;
            }

            bool softCorrected = system.UsesSoftCorrectionTimeline();
            if (!softCorrected)
                system.RunClearFuture(stateTick);
            system.RunReadState(
                stateTick,
                payload,
                baselineTick,
                serverTick);
            if (applyUnityState)
            {
                if (ReferenceEquals(system, hierarchy))
                    ApplyPendingRemoteVisibilityDeletes(stateTick);
                if (!softCorrected)
                    system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
            }
        }

        void ReadAddressedHierarchyRecord(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool fullFrame,
            bool deferUnityApply = false)
        {
            ReadAddressedHierarchy(
                frame,
                stateTick,
                baselineTick,
                serverTick,
                fullFrame,
                deferUnityApply);
        }

        void ReadAddressedStateRecords(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool fullFrame,
            bool eventHandlers)
        {
            ReadAddressedStateSection(
                frame,
                stateTick,
                baselineTick,
                serverTick,
                fullFrame,
                eventHandlers);
        }

        internal void RemovePlayerVisibility(PlayerID player)
        {
            if (_playerVisibility.Remove(player, out var timeline))
                timeline.Clear();
            _hiddenVisibility.Remove(player);
            _dirtyVisibility.Remove(player);
            _hiddenVisibilityAckCandidates.Remove(player);
            _retiredVisibilityRoots.Remove(player);
            _visibilityDependencyRebuild.Remove(player);
            RemoveVisibilityAcquisitions(player);
            RemovePendingVisibilityDeletes(player);
            _hierarchyBaselineScratchByPlayer.Remove(player);
            _hiddenPiecesScratchByPlayer.Remove(player);
            DisposePlayerInputWindowCache(player);
            ClearDesyncTrackingForPlayer(player);
        }

        void ClearVisibilityReplication()
        {
            foreach (var timeline in _playerVisibility.Values)
                timeline.Clear();
            _playerVisibility.Clear();
            _hiddenVisibility.Clear();
            _visibilityAcquisitions.Clear();
            _dirtyVisibility.Clear();
            _hiddenVisibilityAckCandidates.Clear();
            _retiredVisibilityRoots.Clear();
            _visibilityAcquisitionTokens.Clear();
            _visibilityTokenScratch.Clear();
            _visibilityDependencyRebuild.Clear();
            _desiredVisibilityScratch.Clear();
            _hiddenDependencyScratch.Clear();
            _spawnedRootScratch.Clear();
            _ownedRootScratch.Clear();
            _hierarchyBaselineScratchByPlayer.Clear();
            _hiddenPiecesScratchByPlayer.Clear();
            DisposeAllPlayerInputWindowCaches();
            _visibilityRootScratch.Clear();
            ClearPendingVisibilityDeletes();
        }
    }
}
