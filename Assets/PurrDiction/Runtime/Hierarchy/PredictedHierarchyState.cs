using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedHierarchyState : IPredictedData<PredictedHierarchyState>, IDuplicate<PredictedHierarchyState>
    {
        public DisposableList<InstanceDetails> spawnedPrefabs;
        public DisposableList<PredictedObjectID> toDelete;
        public DisposableList<InstanceParent> parents;
        public uint nextInstanceId;

        public PredictedHierarchyState(DisposableList<InstanceDetails> spawnedPrefabs, DisposableList<PredictedObjectID> toDelete, uint nextInstanceId)
            : this(spawnedPrefabs, toDelete, DisposableList<InstanceParent>.Create(4), nextInstanceId)
        {
        }

        public PredictedHierarchyState(DisposableList<InstanceDetails> spawnedPrefabs, DisposableList<PredictedObjectID> toDelete, DisposableList<InstanceParent> parents, uint nextInstanceId)
        {
            this.spawnedPrefabs = spawnedPrefabs;
            this.nextInstanceId = nextInstanceId;
            this.toDelete = toDelete;
            this.parents = parents;
        }

        public void Dispose()
        {
            spawnedPrefabs.Dispose();
            toDelete.Dispose();
            parents.Dispose();
        }

        public PredictedHierarchyState Duplicate()
        {
            return new PredictedHierarchyState(
                spawnedPrefabs.Duplicate(),
                toDelete.Duplicate(),
                parents.Duplicate(),
                nextInstanceId
            );
        }

        public override string ToString()
        {
            if (spawnedPrefabs.isDisposed)
                return $"nextInstanceId={nextInstanceId}";

            string actions = string.Empty;
            for (var i = 0; i < spawnedPrefabs.Count; i++)
            {
                var details = spawnedPrefabs[i];
                actions += $"(prefab: {details.prefabId}, id: {details.instanceId.instanceId})";
                if (i < spawnedPrefabs.Count - 1)
                    actions += "\n";
            }

            if (!parents.isDisposed)
            {
                for (var i = 0; i < parents.Count; i++)
                    actions += $"\n{parents[i]}";
            }

            return $"nextInstanceId={nextInstanceId}\n{actions}";
        }
    }
}
