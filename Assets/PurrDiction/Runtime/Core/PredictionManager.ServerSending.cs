using System;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        /// <summary>
        /// Target rate for ordinary server frames. Zero follows the simulation rate.
        /// </summary>
        public int serverUpdateRate
        {
            get => _serverUpdateRate;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Server update rate cannot be negative.");
                _serverUpdateRate = value;
            }
        }

        /// <summary>
        /// Recent input history every ordinary server update repeats, in milliseconds: the longest
        /// burst of lost updates a client absorbs without waiting for a wider update. Zero repeats
        /// everything since the client's last acknowledged update.
        /// </summary>
        public int serverInputRedundancyMs
        {
            get => _serverInputRedundancyMs;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Input redundancy cannot be negative.");
                _serverInputRedundancyMs = value;
            }
        }

        /// <summary>
        /// <see cref="serverInputRedundancyMs"/> at the current tick rate, rounded up to whole ticks.
        /// Zero when unbounded. Diagnostic only.
        /// </summary>
        public ulong serverInputRedundancyTicks => _serverInputRedundancyMs <= 0
            ? 0
            : (ulong)Math.Ceiling(_serverInputRedundancyMs * Math.Max(1, tickRate) / 1000d);

        private void DispatchPreparedServerFrames()
        {
            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (frame.preparedFrameTick != localTick)
                    continue;

                if (frame.fullFrame || (!isActiveAndEnabled &&
                    frame.frameSendSchedule.ShouldSend(frame.preparedFrameTick, tickRate, serverUpdateRate)))
                {
                    SendPreparedServerFrame(i);
                }
            }
        }

        private void FlushPendingServerFrames()
        {
            if (isSimulating || isReplaying)
                return;
            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (frame.preparedFrameTick != 0 &&
                    frame.frameSendSchedule.ShouldSend(frame.preparedFrameTick, tickRate, serverUpdateRate))
                    SendPreparedServerFrame(i);
            }
        }
    }
}
