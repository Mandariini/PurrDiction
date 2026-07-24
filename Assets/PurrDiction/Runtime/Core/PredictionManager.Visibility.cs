using System;
using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        readonly Dictionary<PlayerID, PlayerVisibilityTimeline> _playerVisibility = new ();
        readonly Dictionary<PlayerID, Dictionary<PredictedObjectID, bool>> _visibilityOverrides = new ();
        readonly HashSet<PlayerID> _preparedVisibilityTopology = new ();
        readonly HashSet<PredictedObjectID> _desiredVisibilityScratch = new ();
        readonly HashSet<PredictedObjectID> _spawnedRootScratch = new ();
        readonly HashSet<PredictedObjectID> _ownedRootScratch = new ();
        readonly HashSet<PredictedObjectID> _hierarchyBaselineRootScratch = new ();
        readonly List<PredictedIdentity> _addressedSystemScratch = new ();

        IPredictionVisibilityProvider _visibilityProvider;

        /// <summary>
        /// Optional server-side visibility predicate. Null preserves the original behavior and
        /// replicates every predicted hierarchy root to every observer.
        /// </summary>
        public IPredictionVisibilityProvider visibilityProvider => _visibilityProvider;

        public void SetVisibilityProvider(IPredictionVisibilityProvider provider)
        {
            _visibilityProvider = provider;
        }

        /// <summary>
        /// Sets a per-player override for one whole spawned root. Passing a piece id resolves
        /// it to its root when the piece currently exists. Overrides take precedence over the
        /// provider, except that receiver-owned roots are always included.
        /// </summary>
        public void SetPlayerVisibility(
            PlayerID player,
            PredictedObjectID objectId,
            bool visible)
        {
            objectId = ResolveVisibilityRoot(objectId);

            if (!_visibilityOverrides.TryGetValue(player, out var overrides))
            {
                overrides = new Dictionary<PredictedObjectID, bool>();
                _visibilityOverrides.Add(player, overrides);
            }

            overrides[objectId] = visible;
        }

        public bool ClearPlayerVisibilityOverride(PlayerID player, PredictedObjectID objectId)
        {
            objectId = ResolveVisibilityRoot(objectId);
            if (!_visibilityOverrides.TryGetValue(player, out var overrides))
                return false;

            bool removed = overrides.Remove(objectId);
            if (overrides.Count == 0)
                _visibilityOverrides.Remove(player);
            return removed;
        }

        public void ClearPlayerVisibilityOverrides(PlayerID player)
        {
            _visibilityOverrides.Remove(player);
        }

        PredictedObjectID ResolveVisibilityRoot(PredictedObjectID objectId)
        {
            if (hierarchy && hierarchy.TryGetRootId(objectId, out var rootId))
                return rootId;
            return objectId;
        }

        PlayerVisibilityTimeline PreparePlayerVisibility(
            PlayerID player,
            ulong tick,
            ulong acknowledgedTick)
        {
            if (!_playerVisibility.TryGetValue(player, out var timeline))
            {
                timeline = new PlayerVisibilityTimeline();
                _playerVisibility.Add(player, timeline);
            }

            AcknowledgePendingVisibilityDeletes(player, acknowledgedTick);

            if (!hierarchy)
            {
                _desiredVisibilityScratch.Clear();
                timeline.Record(tick, _desiredVisibilityScratch);
                return timeline;
            }

            _desiredVisibilityScratch.Clear();
            _spawnedRootScratch.Clear();
            _ownedRootScratch.Clear();

            hierarchy.CollectSpawnedRoots(_spawnedRootScratch);

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system || !system.owner.HasValue || system.owner.Value != player)
                    continue;
                if (system.id.objectId.instanceId.value == 1)
                    continue;

                _ownedRootScratch.Add(ResolveVisibilityRoot(system.id.objectId));
            }

            var provider = _visibilityProvider;
            if (provider is UnityEngine.Object providerObject && !providerObject)
                provider = null;

            foreach (var rootId in _spawnedRootScratch)
            {
                if (_ownedRootScratch.Contains(rootId))
                {
                    _desiredVisibilityScratch.Add(rootId);
                    continue;
                }

                if (TryGetVisibilityOverride(player, rootId, out bool visible))
                {
                    if (visible)
                        _desiredVisibilityScratch.Add(rootId);
                    continue;
                }

                bool include = true;
                if (provider != null)
                {
                    hierarchy.TryGetGameObject(rootId, out var root);
                    try
                    {
                        include = provider.CanSee(player, rootId, root);
                    }
                    catch (Exception exception)
                    {
                        include = true;
                        PurrLogger.LogError(
                            $"Visibility provider failed for player {player}, root {rootId}; " +
                            $"including it for safety. {exception}");
                    }
                }

                if (include)
                    _desiredVisibilityScratch.Add(rootId);
            }

            bool hasPendingDelete = hierarchy.PreserveVisiblePendingDeletes(
                timeline,
                _desiredVisibilityScratch);
            hierarchy.ExpandVisibilityDependencies(_desiredVisibilityScratch);
            timeline.Record(tick, _desiredVisibilityScratch);
            timeline.PruneThrough(acknowledgedTick);

            if (timeline.latestTransitionTick == tick ||
                hasPendingDelete ||
                HasPendingVisibilityDeletes(player))
            {
                _preparedVisibilityTopology.Add(player);
            }

            return timeline;
        }

        bool TryGetVisibilityOverride(
            PlayerID player,
            PredictedObjectID rootId,
            out bool visible)
        {
            if (_visibilityOverrides.TryGetValue(player, out var overrides) &&
                overrides.TryGetValue(rootId, out visible))
            {
                return true;
            }

            visible = default;
            return false;
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
            bool hasHierarchyBaseline)
        {
            if (system.id.objectId.instanceId.value == 1 || !hierarchy)
                return false;

            var rootId = ResolveVisibilityRoot(system.id.objectId);
            return !timeline.HasContinuousVisibilityFrom(rootId, baselineTick) ||
                   !hasHierarchyBaseline ||
                   !_hierarchyBaselineRootScratch.Contains(rootId);
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
            var currentProjection = PredictedHierarchy.BuildVisibilityProjection(
                currentGlobal,
                timeline,
                tick);

            PreparePendingVisibilityDeletesCurrent(
                player,
                tick,
                ref currentProjection);

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
                    baselineProjection = PredictedHierarchy.BuildVisibilityProjection(
                        baselineGlobal,
                        timeline,
                        baselineTick);
                    PreparePendingVisibilityDeletesBaseline(
                        player,
                        baselineTick,
                        ref baselineProjection);
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

            PredictedHierarchyState hierarchyBaseline = default;
            bool hasHierarchyBaseline = hierarchy &&
                                        hierarchy.TryGetVerifiedState(
                                            baselineTick,
                                            out _,
                                            out hierarchyBaseline);
            _hierarchyBaselineRootScratch.Clear();
            if (hasHierarchyBaseline)
            {
                PredictedHierarchy.CollectSpawnedRoots(
                    hierarchyBaseline,
                    _hierarchyBaselineRootScratch);
            }

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system == hierarchy || system.isEventHandler != eventHandlers)
                    continue;
                if (!IsSystemVisible(timeline, system))
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

                bool writeFull = fullFrame ||
                                 RequiresFullEntryState(
                                     timeline,
                                     system,
                                     baselineTick,
                                     hasHierarchyBaseline);

                if (!TryWriteAggregateVisibilityState(
                        system,
                        player,
                        timeline,
                        payload,
                        tick,
                        baselineTick,
                        ref writeFull))
                {
                    if (writeFull)
                        system.RunWriteFirstState(tick, payload);
                    else
                        system.RunWriteCurrentState(player, payload, baselineTick);
                }

                AddressedPredictionRecords.WriteRecord(
                    destination,
                    system.id,
                    writeFull,
                    payload);
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

        void WriteVisibilityInputHistory(
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

            using var payload = BitPackerPool.Get();
            for (ulong tick = from + 1; tick <= localTick; tick++)
            {
                _addressedSystemScratch.Clear();

                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (!system.hasInput ||
                        !system.HasInputAt(tick) ||
                        !WasSystemVisibleAt(timeline, system, tick))
                    {
                        continue;
                    }

                    _addressedSystemScratch.Add(system);
                }

                Packer<PackedUInt>.Write(frame, (uint)_addressedSystemScratch.Count);

                for (var i = 0; i < _addressedSystemScratch.Count; i++)
                {
                    var system = _addressedSystemScratch[i];
                    Packer<PredictedComponentID>.Write(frame, system.id);
                    payload.ResetPositionAndMode(false);
                    system.WriteFirstInput(tick, payload);
                    int bits = payload.positionInBits;
                    Packer<PackedUInt>.Write(frame, (uint)bits);
                    frame.WriteBitsWithoutConsumingIt(payload, bits);
                }
            }
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
                });
        }

        void ReadAddressedStateSection(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool fullFrame,
            bool eventHandlers)
        {
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

                    ApplyAddressedState(
                        system,
                        payload,
                        fullFrame || isFullState,
                        stateTick,
                        baselineTick,
                        serverTick,
                        eventHandlers);
                });
        }

        void ReadAddressedFirstInputSection(BitPacker frame, ulong inputTick)
        {
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, _, payload, _) =>
                {
                    if (_instanceMap.TryGetValue(id, out var system) && system.hasInput)
                        system.ReadFirstInput(inputTick, payload);
                });
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
                    system.RunRollback(stateTick);
                    system.RunResetInterpolation();
                    system.lastVerifiedTick = stateTick;
                }
                return;
            }

            if (applyUnityState &&
                !eventHandler &&
                _validateDeterministicData &&
                system.isDeterministic)
            {
                system.RunRollback(stateTick);
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

        bool HasPreparedVisibilityTopology(PlayerID player)
        {
            return _preparedVisibilityTopology.Contains(player);
        }

        void RemovePlayerVisibility(PlayerID player)
        {
            if (_playerVisibility.Remove(player, out var timeline))
                timeline.Clear();
            _visibilityOverrides.Remove(player);
            _preparedVisibilityTopology.Remove(player);
            RemovePendingVisibilityDeletes(player);
        }

        void ClearVisibilityReplication()
        {
            foreach (var timeline in _playerVisibility.Values)
                timeline.Clear();
            _playerVisibility.Clear();
            _visibilityOverrides.Clear();
            _preparedVisibilityTopology.Clear();
            ClearPendingVisibilityDeletes();
        }
    }
}
