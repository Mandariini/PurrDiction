using System;

namespace PurrNet.Prediction
{
    // An ordinary frame repeated fewer input ticks than this peer is behind. Nothing was applied;
    // the server extends the window once the stalled acknowledgement shows the gap.
    internal sealed class InputHistoryWindowException : InvalidOperationException
    {
        public InputHistoryWindowException(ulong serverTick, ulong firstInputTick, ulong verifiedTick)
            : base($"Frame {serverTick} repeats inputs from tick {firstInputTick} but tick {verifiedTick + 1} was never verified.")
        {
        }
    }
}
