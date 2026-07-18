using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct InstanceParent : IPackedAuto, IEquatable<InstanceParent>
    {
        public readonly PredictedObjectID child;
        public readonly PredictedComponentID parent;

        public InstanceParent(PredictedObjectID child, PredictedComponentID parent)
        {
            this.child = child;
            this.parent = parent;
        }

        public bool Equals(InstanceParent other)
        {
            return child.Equals(other.child) && parent.Equals(other.parent);
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
            return $"{child} -> {parent}";
        }
    }
}
