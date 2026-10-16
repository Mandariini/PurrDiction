using PurrNet.Packing;
using PurrNet.Modules;

namespace PurrNet.Prediction
{
    // History snapshots repeat ownership on every entry. Store the nullable ID without
    // Nullable<PlayerID>'s padding while retaining the public metadata and its wire codec.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    internal struct CompactIdentityState : IPackedAuto
    {
        private ulong _ownerId;
        private byte _ownerFlags;
        public bool wasOnSimulationStartCalled;

        public PlayerID? owner
        {
            get => (_ownerFlags & 1) != 0 ? new PlayerID(_ownerId, (_ownerFlags & 2) != 0) : (PlayerID?)null;
            set
            {
                _ownerId = OwnerId(value);
                _ownerFlags = OwnerFlags(value);
            }
        }

        internal static ulong OwnerId(PlayerID? owner)
            => owner.HasValue ? owner.GetValueOrDefault().id.value : 0;

        internal static byte OwnerFlags(PlayerID? owner)
            => owner.HasValue ? (byte)(1 | (owner.GetValueOrDefault().isBot ? 2 : 0)) : (byte)0;

        // Takes an owner already encoded by OwnerId and OwnerFlags.
        internal void SetOwner(ulong ownerId, byte ownerFlags)
        {
            _ownerId = ownerId;
            _ownerFlags = ownerFlags;
        }

        public static implicit operator PredictedIdentityState(CompactIdentityState value)
            => new PredictedIdentityState { owner = value.owner, wasOnSimulationStartCalled = value.wasOnSimulationStartCalled };

        public static implicit operator CompactIdentityState(PredictedIdentityState value)
            => new CompactIdentityState { owner = value.owner, wasOnSimulationStartCalled = value.wasOnSimulationStartCalled };

        public void Dispose() { }

        // Both conversions and the owner setter produce canonical flags and a zero ID
        // for an absent owner. Compare those fields without rebuilding nullable IDs.
        public bool HasSameContents(in CompactIdentityState other)
            => _ownerId == other._ownerId && _ownerFlags == other._ownerFlags &&
               wasOnSimulationStartCalled == other.wasOnSimulationStartCalled;

        public override string ToString() => ((PredictedIdentityState)this).ToString();
    }

    internal static class CompactIdentityStatePacking
    {
        [UsedByIL]
        public static void Write(BitPacker packer, CompactIdentityState value)
            => Packer<PredictedIdentityState>.Write(packer, value);

        [UsedByIL]
        public static void Read(BitPacker packer, ref CompactIdentityState value)
        {
            PredictedIdentityState metadata = default;
            Packer<PredictedIdentityState>.Read(packer, ref metadata);
            value = metadata;
        }

        [UsedByIL]
        public static bool WriteDelta(BitPacker packer, CompactIdentityState baseline, CompactIdentityState value)
            => DeltaPacker<PredictedIdentityState>.Write(packer, baseline, value);

        [UsedByIL]
        public static void ReadDelta(BitPacker packer, CompactIdentityState baseline, ref CompactIdentityState value)
        {
            PredictedIdentityState metadata = value;
            DeltaPacker<PredictedIdentityState>.Read(packer, baseline, ref metadata);
            value = metadata;
        }
    }
}
