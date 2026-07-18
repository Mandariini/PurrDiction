using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    /// <summary>
    /// A recorded deviation from a piece's authored default parent.
    /// A null parent means the piece was explicitly moved to the scene root.
    /// Pieces sitting at their default parent have no entry at all.
    /// </summary>
    public readonly struct InstanceParent : IPackedAuto, IEquatable<InstanceParent>
    {
        public readonly PredictedObjectID child;
        public readonly PredictedComponentID? parent;

        public InstanceParent(PredictedObjectID child, PredictedComponentID? parent)
        {
            this.child = child;
            this.parent = parent;
        }

        public bool Equals(InstanceParent other)
        {
            return child.Equals(other.child) && Nullable.Equals(parent, other.parent);
        }

        public override bool Equals(object obj)
        {
            return obj is InstanceParent other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(child, parent);
        }

        public override string ToString()
        {
            return $"{child} -> {(parent.HasValue ? parent.Value.ToString() : "root")}";
        }
    }
}
