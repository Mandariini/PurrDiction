using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;
        public bool fullFrame;
        public ulong preparedFrameTick;
        public ulong preparedVisibilityTick;
        public ulong sentVisibilityTick;
        public int maxUnreliableFrameBytes;
        public ReliableFrameDeliveryState reliableFrame;
        public BaselineAdvanceTracker baselineAdvance;

        public void Dispose()
        {
            packer?.Dispose();
            preparedFrameTick = 0;
            preparedVisibilityTick = 0;
            sentVisibilityTick = 0;
            reliableFrame.Clear();
            baselineAdvance.Reset();
        }
    }

    /// <summary>
    /// Detects a failing server-to-client frame path by measuring how fast a client's acked
    /// baseline advances relative to the server tick. A healthy link advances the baseline
    /// roughly once per tick regardless of latency; when frames stop being applied the
    /// baseline stalls while the server keeps ticking.
    /// </summary>
    internal struct BaselineAdvanceTracker
    {
        private ulong _windowStartTick;
        private ulong _windowStartBaseline;

        public bool distressed { get; private set; }

        public void Observe(ulong localTick, ulong baselineTick, ulong windowTicks)
        {
            if (baselineTick == 0)
            {
                Reset();
                _windowStartTick = localTick;
                return;
            }

            if (_windowStartTick == 0 || baselineTick < _windowStartBaseline ||
                localTick < _windowStartTick)
            {
                _windowStartTick = localTick;
                _windowStartBaseline = baselineTick;
                return;
            }

            ulong elapsed = localTick - _windowStartTick;
            if (elapsed < windowTicks)
                return;

            ulong advanced = baselineTick - _windowStartBaseline;
            distressed = advanced * 2 < elapsed;
            _windowStartTick = localTick;
            _windowStartBaseline = baselineTick;
        }

        public void Reset()
        {
            _windowStartTick = 0;
            _windowStartBaseline = 0;
            distressed = false;
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
