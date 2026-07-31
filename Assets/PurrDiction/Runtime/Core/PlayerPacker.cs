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
        public ulong reliableSentAtLocalTick;
        public ulong reliableSentBaselineTick;
        public ulong reliableSentInputAck;
        public bool reliableSentFullFrame;
        public ReliableFrameDeliveryState reliableFrame;
        public BaselineAdvanceTracker baselineAdvance;

        public void Dispose()
        {
            packer?.Dispose();
            preparedFrameTick = 0;
            preparedVisibilityTick = 0;
            sentVisibilityTick = 0;
            reliableSentAtLocalTick = 0;
            reliableSentBaselineTick = 0;
            reliableSentInputAck = 0;
            reliableSentFullFrame = false;
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
        // After a distress episode the link is likely to relapse as soon as regular deltas
        // resume; re-evaluating on a fraction of the window keeps the recovery cadence bound
        // to the ack round trip instead of the full detection window.
        internal const ulong FastRearmWindowDivisor = 8;
        internal const ulong MinFastRearmWindowTicks = 4;
        internal const byte FastRearmHealthyWindowsToDecay = 16;

        private ulong _windowStartTick;
        private ulong _windowStartBaseline;
        private byte _fastRearmWindows;

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

            if (_fastRearmWindows > 0)
            {
                var fastWindow = windowTicks / FastRearmWindowDivisor;
                if (fastWindow < MinFastRearmWindowTicks)
                    fastWindow = MinFastRearmWindowTicks;
                if (fastWindow < windowTicks)
                    windowTicks = fastWindow;
            }

            ulong elapsed = localTick - _windowStartTick;
            if (elapsed < windowTicks)
                return;

            ulong advanced = baselineTick - _windowStartBaseline;
            distressed = advanced * 2 < elapsed;

            if (distressed)
                _fastRearmWindows = FastRearmHealthyWindowsToDecay;
            else if (_fastRearmWindows > 0)
                _fastRearmWindows--;

            _windowStartTick = localTick;
            _windowStartBaseline = baselineTick;
        }

        public void Reset()
        {
            _windowStartTick = 0;
            _windowStartBaseline = 0;
            _fastRearmWindows = 0;
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
