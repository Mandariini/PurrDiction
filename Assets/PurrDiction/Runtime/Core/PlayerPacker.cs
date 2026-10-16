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
        public ulong lastFullFrameSentTick;
        public ulong lastSentFrameTick;
        // The receiver keeps the complete input roster of the last transcript tick it applied,
        // so the next frame's first tick can repeat it. Holds only for unfiltered transcripts.
        public ulong preparedInputCarryTick;
        public bool preparedInputCarry;
        public ulong sentInputCarryTick;
        public bool sentInputCarry;

        public void BeginFullFrame(ulong tick)
        {
            lastFullFrameSentTick = tick;
            requiresFullCheckpoint = false;
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
            lastFullFrameSentTick = 0;
            lastSentFrameTick = 0;
            preparedInputCarry = false;
            sentInputCarry = false;
        }
    }
}
