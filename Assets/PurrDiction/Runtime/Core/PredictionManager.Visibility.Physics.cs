using System;
using System.Collections.Generic;
using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        static readonly ProfilerMarker BuildVisibilityPhysics3DProjectionMarker =
            new("PredictionManager.BuildVisibilityPhysics3DProjection");
        static readonly ProfilerMarker BuildVisibilityPhysics2DProjectionMarker =
            new("PredictionManager.BuildVisibilityPhysics2DProjection");

        readonly HashSet<PredictedObjectID> _hiddenPiecesScratch = new ();

        bool TryWriteAggregateVisibilityState(
            PredictedIdentity system,
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull,
            out bool changed)
        {
            changed = false;
            if (!hierarchy)
                return false;

#if UNITY_PHYSICS_3D
            if (system == physics3d)
            {
                changed = WritePhysics3DVisibilityState(
                    receiver,
                    timeline,
                    payload,
                    tick,
                    baselineTick,
                    ref writeFull);
                return true;
            }
#endif

#if UNITY_PHYSICS_2D
            if (system == physics2d)
            {
                changed = WritePhysics2DVisibilityState(
                    receiver,
                    timeline,
                    payload,
                    tick,
                    baselineTick,
                    ref writeFull);
                return true;
            }
#endif

            return false;
        }

        void CollectHiddenPieces(
            in PredictedHierarchyState hierarchyState,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            _hiddenPiecesScratch.Clear();

            if (!hierarchyState.spawnedPrefabs.isDisposed)
            {
                bool hasPreviousRoot = false;
                bool previousRootVisible = false;
                PredictedObjectID previousRoot = default;

                for (var i = 0; i < hierarchyState.spawnedPrefabs.Count; i++)
                {
                    var record = hierarchyState.spawnedPrefabs[i];
                    var rootId = record.rootId;
                    if (!hasPreviousRoot || !rootId.Equals(previousRoot))
                    {
                        previousRoot = rootId;
                        previousRootVisible = timeline.WasVisibleAt(rootId, tick);
                        hasPreviousRoot = true;
                    }

                    if (!previousRootVisible)
                        _hiddenPiecesScratch.Add(record.instanceId);
                }
            }

            timeline.CollectHiddenRootsAt(tick, _hiddenPiecesScratch);
        }

#if UNITY_PHYSICS_3D
        bool WritePhysics3DVisibilityState(
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull)
        {
            physics3d.RefreshVerifiedFromLive(tick);
            if (!physics3d.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedPhysicsData currentGlobal))
            {
                throw new InvalidOperationException(
                    $"No authoritative 3D physics state exists for tick {tick}.");
            }

            // A pass-through receiver hides nothing, so the projection would copy every event
            // verbatim. Feed the shared global data straight through instead of duplicating the
            // whole event list once per receiver per tick.
            if (timeline.isPassThrough)
            {
                if (!writeFull &&
                    physics3d.TryGetVerifiedState(
                        baselineTick,
                        out var directBaselinePrediction,
                        out PredictedPhysicsData directBaselineGlobal))
                {
                    return physics3d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        directBaselinePrediction,
                        directBaselineGlobal,
                        currentGlobal);
                }

                writeFull = true;
                physics3d.RunWriteFirstProjectedState(
                    tick,
                    payload,
                    currentGlobal);
                return true;
            }

            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative hierarchy state exists for 3D physics tick {tick}.");
            }

            PredictedPhysicsData currentProjection;
            using (BuildVisibilityPhysics3DProjectionMarker.Auto())
            {
                CollectHiddenPieces(currentHierarchy, timeline, tick);
                currentProjection = PredictionPhysicsVisibility.Project(
                    currentGlobal,
                    _hiddenPiecesScratch);
            }
            PredictedPhysicsData baselineProjection = default;
            bool changed;

            try
            {
                if (!writeFull &&
                    physics3d.TryGetVerifiedState(
                        baselineTick,
                        out var baselinePrediction,
                        out PredictedPhysicsData baselineGlobal) &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out _,
                        out PredictedHierarchyState baselineHierarchy))
                {
                    using (BuildVisibilityPhysics3DProjectionMarker.Auto())
                    {
                        CollectHiddenPieces(
                            baselineHierarchy,
                            timeline,
                            baselineTick);
                        baselineProjection = PredictionPhysicsVisibility.Project(
                            baselineGlobal,
                            _hiddenPiecesScratch);
                    }
                    changed = physics3d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        baselinePrediction,
                        baselineProjection,
                        currentProjection);
                }
                else
                {
                    writeFull = true;
                    physics3d.RunWriteFirstProjectedState(
                        tick,
                        payload,
                        currentProjection);
                    changed = true;
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }

            return changed;
        }
#endif

#if UNITY_PHYSICS_2D
        bool WritePhysics2DVisibilityState(
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull)
        {
            physics2d.RefreshVerifiedFromLive(tick);
            if (!physics2d.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedPhysics2DData currentGlobal))
            {
                throw new InvalidOperationException(
                    $"No authoritative 2D physics state exists for tick {tick}.");
            }

            // See WritePhysics3DVisibilityState: nothing is hidden for a pass-through receiver,
            // so the projection is the identity and the global data can be sent directly.
            if (timeline.isPassThrough)
            {
                if (!writeFull &&
                    physics2d.TryGetVerifiedState(
                        baselineTick,
                        out var directBaselinePrediction,
                        out PredictedPhysics2DData directBaselineGlobal))
                {
                    return physics2d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        directBaselinePrediction,
                        directBaselineGlobal,
                        currentGlobal);
                }

                writeFull = true;
                physics2d.RunWriteFirstProjectedState(
                    tick,
                    payload,
                    currentGlobal);
                return true;
            }

            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative hierarchy state exists for 2D physics tick {tick}.");
            }

            PredictedPhysics2DData currentProjection;
            using (BuildVisibilityPhysics2DProjectionMarker.Auto())
            {
                CollectHiddenPieces(currentHierarchy, timeline, tick);
                currentProjection = PredictionPhysicsVisibility.Project(
                    currentGlobal,
                    _hiddenPiecesScratch);
            }
            PredictedPhysics2DData baselineProjection = default;
            bool changed;

            try
            {
                if (!writeFull &&
                    physics2d.TryGetVerifiedState(
                        baselineTick,
                        out var baselinePrediction,
                        out PredictedPhysics2DData baselineGlobal) &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out _,
                        out PredictedHierarchyState baselineHierarchy))
                {
                    using (BuildVisibilityPhysics2DProjectionMarker.Auto())
                    {
                        CollectHiddenPieces(
                            baselineHierarchy,
                            timeline,
                            baselineTick);
                        baselineProjection = PredictionPhysicsVisibility.Project(
                            baselineGlobal,
                            _hiddenPiecesScratch);
                    }
                    changed = physics2d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        baselinePrediction,
                        baselineProjection,
                        currentProjection);
                }
                else
                {
                    writeFull = true;
                    physics2d.RunWriteFirstProjectedState(
                        tick,
                        payload,
                        currentProjection);
                    changed = true;
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }

            return changed;
        }
#endif
    }
}
