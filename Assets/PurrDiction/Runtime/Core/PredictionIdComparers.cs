using System.Collections.Generic;

namespace PurrNet.Prediction
{
    // Unity's IL2CPP Dictionary value-key path repeatedly resolves Default when the
    // comparer is null. Explicit instances keep the same ID equality without that lookup.
    internal static class PredictionIdComparers
    {
        internal static readonly IEqualityComparer<PredictedObjectID> Objects = new ObjectComparer();
        internal static readonly IEqualityComparer<PredictedComponentID> Components = new ComponentComparer();
        internal static readonly IEqualityComparer<ulong> Ticks = new TickComparer();
        internal static readonly IEqualityComparer<PlayerID> Players = new PlayerComparer();

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

        private sealed class TickComparer : IEqualityComparer<ulong>
        {
            public bool Equals(ulong x, ulong y) => x == y;
            public int GetHashCode(ulong value) => value.GetHashCode();
        }

        private sealed class PlayerComparer : IEqualityComparer<PlayerID>
        {
            public bool Equals(PlayerID x, PlayerID y) => x.Equals(y);
            public int GetHashCode(PlayerID value) => value.GetHashCode();
        }
    }
}
