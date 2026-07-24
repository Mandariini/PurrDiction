using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;
        public bool fullFrame;
        public ulong preparedFrameTick;
        public int maxUnreliableFrameBytes;
        public ReliableFrameDeliveryState reliableFrame;

        public void Dispose()
        {
            packer?.Dispose();
            preparedFrameTick = 0;
            reliableFrame.Clear();
        }
    }

    internal struct ReliableFrameDeliveryState
    {
        private ulong _sentTick;

        public bool ShouldSuppress(ulong ackedTick)
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
            _sentTick = tick;
        }

        public void Clear()
        {
            _sentTick = 0;
        }
    }
}
