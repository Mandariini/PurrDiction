using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public sealed class StaticPredictedIdentity : PredictedIdentity
    {
        public override void WriteFirstInput(ulong localTick, BitPacker packer) { }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) { }

        internal override void ClearFuture(ulong stateTick) { }

        internal override void SimulateTick(ulong tick, float delta)
        {
        }

        internal override void LateSimulateTick(float delta)
        {
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate)
        {
        }

        internal override void SaveStateInHistory(ulong tick)
        {
        }

        internal override void Rollback(ulong tick)
        {
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
        }

        public override void ResetInterpolation()
        {
        }

        internal override void GetLatestUnityState()
        {
        }

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var metadata = new PredictedIdentityState { owner = owner };
            Packer<PredictedIdentityState>.Write(packer, metadata);
        }

        internal override bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            var metadata = new PredictedIdentityState { owner = owner };
            return WritePredictionMetadata(receiver, packer, deltaModule, metadata);
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable)
        {
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer)
        {
            PredictedIdentityState metadata = default;
            Packer<PredictedIdentityState>.Read(packer, ref metadata);
            SetOwner(metadata.owner);
        }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            PredictedIdentityState metadata = default;
            ReadPredictionMetadata(packer, deltaModule, ref metadata);
            SetOwner(metadata.owner);
        }

        internal override void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable)
        {
        }
    }
}
