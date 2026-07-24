using System.Collections.Generic;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    internal static class PredictionPhysicsVisibility
    {
        static bool IsVisible(
            PredictedComponentID id,
            HashSet<PredictedObjectID> visiblePieces)
        {
            return id.objectId.instanceId.value == 1 ||
                   visiblePieces.Contains(id.objectId);
        }

#if UNITY_PHYSICS_3D
        public static PredictedPhysicsData Project(
            in PredictedPhysicsData source,
            HashSet<PredictedObjectID> visiblePieces)
        {
            int count = source.events.isDisposed ? 0 : source.events.Count;
            var events = DisposableList<PhysicsEvent>.Create(count);

            for (var i = 0; i < count; i++)
            {
                var physicsEvent = source.events[i];
                if (IsVisible(physicsEvent.me, visiblePieces) &&
                    IsVisible(physicsEvent.other, visiblePieces))
                {
                    events.Add(physicsEvent.Duplicate());
                }
            }

            return new PredictedPhysicsData
            {
                events = events
            };
        }
#endif

#if UNITY_PHYSICS_2D
        public static PredictedPhysics2DData Project(
            in PredictedPhysics2DData source,
            HashSet<PredictedObjectID> visiblePieces)
        {
            int count = source.events.isDisposed ? 0 : source.events.Count;
            var events = DisposableList<Physics2DEvent>.Create(count);

            for (var i = 0; i < count; i++)
            {
                var physicsEvent = source.events[i];
                if (IsVisible(physicsEvent.me, visiblePieces) &&
                    IsVisible(physicsEvent.other, visiblePieces))
                {
                    events.Add(physicsEvent.Duplicate());
                }
            }

            return new PredictedPhysics2DData
            {
                events = events
            };
        }
#endif
    }
}
