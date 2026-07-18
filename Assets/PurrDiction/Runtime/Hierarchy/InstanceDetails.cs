using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly PackedInt prefabId;
        public readonly PackedUInt pieceIndex;
        public readonly PredictedObjectID instanceId;
        public readonly Vector3 spawnPosition;
        public readonly Quaternion spawnRotation;
        public readonly PlayerID? owner;
        public readonly PredictedComponentID? parent;

        /// <summary>
        /// Id of the root piece of the spawn instance this piece belongs to.
        /// Piece ids are allocated as one contiguous block per spawn, so this is derived.
        /// </summary>
        public PredictedObjectID rootId => new PredictedObjectID(instanceId.instanceId.value - pieceIndex.value);

        public bool isRootRecord => pieceIndex.value == 0;

        public InstanceDetails(int prefabId, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner)
            : this(prefabId, 0, instanceId, spawnPosition, spawnRotation, owner, null)
        {
        }

        public InstanceDetails(int prefabId, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner, PredictedComponentID? parent)
            : this(prefabId, 0, instanceId, spawnPosition, spawnRotation, owner, parent)
        {
        }

        public InstanceDetails(int prefabId, uint pieceIndex, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner, PredictedComponentID? parent)
        {
            this.prefabId = prefabId;
            this.pieceIndex = pieceIndex;
            this.instanceId = instanceId;
            this.spawnPosition = spawnPosition;
            this.spawnRotation = spawnRotation;
            this.owner = owner;
            this.parent = parent;
        }

        public bool Equals(InstanceDetails other)
        {
            return prefabId == other.prefabId && pieceIndex.value == other.pieceIndex.value &&
                   instanceId.Equals(other.instanceId) &&
                   spawnPosition.Equals(other.spawnPosition) &&
                   spawnRotation.Equals(other.spawnRotation) && owner == other.owner &&
                   Nullable.Equals(parent, other.parent);
        }

        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, pieceIndex, instanceId, spawnPosition, spawnRotation, owner, parent);
        }

        public override string ToString()
        {
            return $"id: {instanceId} (piece {pieceIndex.value} of {rootId}), {spawnPosition} | {spawnRotation}{(parent.HasValue ? $" | parent: {parent.Value}" : string.Empty)}\n";
        }
    }
}
