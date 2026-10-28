using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Obsolete("Use PredictedPrefab.pooled and PredictedPrefab.warmupCount instead.")]
    [Serializable]
    public struct PoolSettings
    {
        public bool usePooling;
        public int initialSize;
    }

    [Serializable]
    public struct PredictedPrefab
    {
        public string guid;
        public GameObject prefab;
        public bool pooled;
        public int warmupCount;

#pragma warning disable CS0618
        [Obsolete("Use pooled and warmupCount instead.")]
        public PoolSettings pooling
        {
            get => new PoolSettings { usePooling = pooled, initialSize = warmupCount };
            set
            {
                pooled = value.usePooling;
                warmupCount = value.initialSize;
            }
        }
#pragma warning restore CS0618
    }
}
