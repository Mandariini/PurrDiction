using System.Collections.Generic;

namespace PurrNet.Prediction
{
    // Unity's IL2CPP Dictionary value-key path repeatedly resolves Default when the
    // comparer is null. Explicit instances keep the same ID equality without that lookup.
    internal static class PredictionIdComparers
    {
        internal static readonly IEqualityComparer<PredictedObjectID> Objects = new ObjectComparer();
        internal static readonly IEqualityComparer<PredictedComponentID> Components = new ComponentComparer();

        private sealed class ObjectComparer : IEqualityComparer<PredictedObjectID>
        {
            public bool Equals(PredictedObjectID x, PredictedObjectID y) => x.Equals(y);
            public int GetHashCode(PredictedObjectID value) => value.GetHashCode();
        }

        private sealed class ComponentComparer : IEqualityComparer<PredictedComponentID>
        {
            public bool Equals(PredictedComponentID x, PredictedComponentID y) => x.Equals(y);
            public int GetHashCode(PredictedComponentID value) => value.GetHashCode();
        }
    }
}
