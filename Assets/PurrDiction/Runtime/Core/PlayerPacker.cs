using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;
        public bool fullFrame;
        public bool requiresFullCheckpoint;
        public ulong preparedFrameTick;
        public ServerFrameSendSchedule frameSendSchedule;
        public ulong preparedBaselineTick;
        public ulong preparedVisibilityTick;
        public ulong sentVisibilityTick;
        public int maxUnreliableFrameBytes;
        public ulong reliableSentAtLocalTick;
        public ulong lastFullFrameSentTick;
        public ReliableFrameDeliveryState reliableFrame;

        public void BeginFullFrame(ulong tick)
        {
            reliableFrame.MarkSent(tick);
            reliableSentAtLocalTick = tick;
            lastFullFrameSentTick = tick;
            requiresFullCheckpoint = false;
        }

        // Clear only after ACK or delivery proof, never for a queued snapshot request.
        // The checkpoint epoch remains valid for subsequent unreliable deltas.
        public void ClearRecoveryFrame()
        {
            reliableFrame.Clear();
            reliableSentAtLocalTick = 0;
        }

        public void Dispose()
        {
            packer?.Dispose();
            packer = null;
            preparedFrameTick = 0;
            frameSendSchedule = default;
            preparedBaselineTick = 0;
            preparedVisibilityTick = 0;
            sentVisibilityTick = 0;
            requiresFullCheckpoint = false;
            ClearRecoveryFrame();
            lastFullFrameSentTick = 0;
        }
    }

    // Only one full checkpoint can be in flight. Its continuation deltas cannot be ACKed
    // before it is applied, so their ACK also proves delivery of the full RPC.
    internal struct ReliableFrameDeliveryState
    {
        private ulong _sentTick;
        public ulong pendingTick => _sentTick;

        public bool IsPending(ulong ackedTick)
        {
            if (_sentTick == 0)
                return false;

            if (ackedTick < _sentTick)
                return true;

            _sentTick = 0;
            return false;
        }

        public void MarkSent(ulong tick)
        {
            if (tick == 0)
                throw new System.ArgumentOutOfRangeException(nameof(tick));
            if (_sentTick != 0)
                throw new System.InvalidOperationException("A full checkpoint is already awaiting acknowledgement.");

            _sentTick = tick;
        }

        public void Clear()
        {
            _sentTick = 0;
        }
    }
}
