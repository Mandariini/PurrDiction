using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// A GameObject about to enter a prediction world, raised by <see cref="PredictedHierarchy.onPreSpawn"/>
    /// before any of its predicted identities run their setup.
    /// </summary>
    public readonly struct PredictedPreSpawn
    {
        /// <summary>The root GameObject of the instance (the scene object root for scene objects).</summary>
        public readonly GameObject gameObject;

        /// <summary>The world the instance is spawning into.</summary>
        public readonly PredictionManager predictionManager;

        /// <summary>True for objects placed in the scene, false for prefab instances.</summary>
        public readonly bool isSceneObject;

        /// <summary>True when the prefab instance came out of the prefab pool rather than a fresh Instantiate.</summary>
        public readonly bool fromPool;

        public PredictedPreSpawn(GameObject gameObject, PredictionManager predictionManager, bool isSceneObject, bool fromPool)
        {
            this.gameObject = gameObject;
            this.predictionManager = predictionManager;
            this.isSceneObject = isSceneObject;
            this.fromPool = fromPool;
        }
    }
}
