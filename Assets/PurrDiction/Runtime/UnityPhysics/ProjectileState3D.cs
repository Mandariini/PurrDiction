using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct ProjectileState3D : IPredictedData<ProjectileState3D>
    {
        public Vector3 velocity;
        public float gravity;
        public float radius;
        public bool isTrigger;
        public DisposableList<PredictedComponentID> overlappingTriggers;
        public PredictedComponentID lastSolidContact;
        public bool hasLastSolidContact;

        public void Dispose()
        {
            overlappingTriggers.Dispose();
        }

        public override string ToString()
        {
            return $"Velocity: {velocity}\nGravity: {gravity}\nRadius: {radius}\nIsTrigger: {isTrigger}";
        }
    }
}
