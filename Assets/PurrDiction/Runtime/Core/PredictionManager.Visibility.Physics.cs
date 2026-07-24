using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        readonly HashSet<PredictedObjectID> _visiblePiecesScratch = new ();

        bool TryWriteAggregateVisibilityState(
            PredictedIdentity system,
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull)
        {
            if (!hierarchy)
                return false;

#if UNITY_PHYSICS_3D
            if (system == physics3d)
            {
                WritePhysics3DVisibilityState(
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
                WritePhysics2DVisibilityState(
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

        void CollectVisiblePieces(
            in PredictedHierarchyState hierarchyState,
            PlayerVisibilityTimeline timeline,
            ulong tick)
        {
            _visiblePiecesScratch.Clear();
            if (hierarchyState.spawnedPrefabs.isDisposed)
                return;

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

                if (previousRootVisible)
                    _visiblePiecesScratch.Add(record.instanceId);
            }
        }

#if UNITY_PHYSICS_3D
        void WritePhysics3DVisibilityState(
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
                    out PredictedPhysicsData currentGlobal) ||
                !hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative 3D physics projection exists for tick {tick}.");
            }

            CollectVisiblePieces(currentHierarchy, timeline, tick);
            var currentProjection = PredictionPhysicsVisibility.Project(
                currentGlobal,
                _visiblePiecesScratch);
            PredictedPhysicsData baselineProjection = default;

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
                    CollectVisiblePieces(
                        baselineHierarchy,
                        timeline,
                        baselineTick);
                    baselineProjection = PredictionPhysicsVisibility.Project(
                        baselineGlobal,
                        _visiblePiecesScratch);
                    physics3d.RunWriteProjectedState(
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
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }
        }
#endif

#if UNITY_PHYSICS_2D
        void WritePhysics2DVisibilityState(
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
                    out PredictedPhysics2DData currentGlobal) ||
                !hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative 2D physics projection exists for tick {tick}.");
            }

            CollectVisiblePieces(currentHierarchy, timeline, tick);
            var currentProjection = PredictionPhysicsVisibility.Project(
                currentGlobal,
                _visiblePiecesScratch);
            PredictedPhysics2DData baselineProjection = default;

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
                    CollectVisiblePieces(
                        baselineHierarchy,
                        timeline,
                        baselineTick);
                    baselineProjection = PredictionPhysicsVisibility.Project(
                        baselineGlobal,
                        _visiblePiecesScratch);
                    physics2d.RunWriteProjectedState(
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
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }
        }
#endif
    }
}
