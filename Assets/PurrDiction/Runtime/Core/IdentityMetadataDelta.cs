using PurrNet.Packing;

namespace PurrNet.Prediction
{
    // Identity metadata rarely changes, yet every state record carries its delta. The generated
    // struct codec encodes an unchanged value as one clear flag bit and a changed value with a set
    // flag first, so compare the compact fields directly and leave changes to that codec.
    // Both directions stay bit-identical to it; a startup probe disables the shortcut otherwise.
    internal static class IdentityMetadataDelta
    {
        private static int _shortcut;

        private static bool shortcut => _shortcut != 0 ? _shortcut > 0 : Probe();

        internal static bool Write(BitPacker packer, in CompactIdentityState baseline, in CompactIdentityState value)
        {
            if (shortcut && baseline.HasSameContents(value))
            {
                packer.WriteBit(false);
                return false;
            }

            return DeltaPacker<PredictedIdentityState>.Write(packer, baseline, value);
        }

        internal static CompactIdentityState Read(BitPacker packer, in CompactIdentityState baseline)
        {
            if (shortcut)
            {
                int origin = packer.positionInBits;
                if (!packer.ReadBit())
                    return baseline;
                packer.SetBitPosition(origin);
            }

            PredictedIdentityState metadata = default;
            DeltaPacker<PredictedIdentityState>.Read(packer, baseline, ref metadata);
            return metadata;
        }

        private static bool Probe()
        {
            var owned = new PredictedIdentityState { owner = new PlayerID(7UL, false), wasOnSimulationStartCalled = true };
            var unowned = new PredictedIdentityState { owner = null, wasOnSimulationStartCalled = false };
            using var packer = BitPackerPool.Get();
            bool matches = !DeltaPacker<PredictedIdentityState>.Write(packer, owned, owned) &&
                           packer.positionInBits == 1;
            if (matches)
            {
                packer.ResetPositionAndMode(true);
                matches = !packer.ReadBit();
            }

            if (matches)
            {
                packer.ResetPositionAndMode(false);
                matches = DeltaPacker<PredictedIdentityState>.Write(packer, owned, unowned);
                packer.ResetPositionAndMode(true);
                matches &= packer.ReadBit();
            }

            _shortcut = matches ? 1 : -1;
            return matches;
        }
    }
}
