using System;
using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private const double HistoryResyncRetrySeconds = 1d;
        private bool _historyResyncPending;
        private ulong _historyResyncRequiredAfterTick;
        private double _nextHistoryResyncRequestAt;
        private ulong _applyingFrameServerTick;
        private readonly Dictionary<PlayerID, double> _historyResyncServedAt = new();

        private void MarkHistoryResyncNeeded(ulong failedTick)
        {
            _historyResyncPending = true;
            _historyResyncRequiredAfterTick = Math.Max(_historyResyncRequiredAfterTick, failedTick);
            SendPendingHistoryResyncRequest();
        }

        private bool TryTakeHistoryResyncRequest(double now)
        {
            if (!_historyResyncPending || now < _nextHistoryResyncRequestAt)
                return false;

            _nextHistoryResyncRequestAt = now + HistoryResyncRetrySeconds;
            return true;
        }

        private void SendPendingHistoryResyncRequest()
        {
            if (!isSpawned || !isClient || isServer ||
                !TryTakeHistoryResyncRequest(Time.unscaledTimeAsDouble))
                return;

            TraceHistoryResync("Request", localPlayer ?? default, _historyResyncRequiredAfterTick);
            RequestHistoryResync(_historyResyncRequiredAfterTick, _appliedCheckpointTick, _rejectedCheckpointTick);
        }

        private void CompleteHistoryResync(ulong fullTick)
        {
            if (!_historyResyncPending || fullTick < _historyResyncRequiredAfterTick)
                return;

            _historyResyncPending = false;
            _historyResyncRequiredAfterTick = 0;
            _nextHistoryResyncRequestAt = 0;
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestHistoryResync(ulong failedTick, ulong appliedCheckpointTick,
            ulong rejectedCheckpointTick, RPCInfo info = default)
            => HandleHistoryResyncRequest(info.sender, failedTick, appliedCheckpointTick, rejectedCheckpointTick);

        private void HandleHistoryResyncRequest(PlayerID player, ulong failedTick,
            ulong appliedCheckpointTick, ulong rejectedCheckpointTick)
        {
            if (!_clientTicks.TryGetValue(player, out var input) || failedTick > localTick)
                return;

            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (!frame.player.Equals(player))
                    continue;

                // Delivery proof releases the pending checkpoint; only successful application advances its ACK.
                ulong pendingTick = frame.reliableFrame.pendingTick;
                bool rejected = pendingTick != 0 && rejectedCheckpointTick == pendingTick;
                if (pendingTick != 0 && (appliedCheckpointTick == pendingTick || rejected))
                {
                    if (!rejected)
                        input.ackedServerTick = Math.Max(input.ackedServerTick, pendingTick);
                    frame.ClearRecoveryFrame();
                    _clientFrames[i] = frame;
                }

                // A partial delta cannot complete a repair that requires a full snapshot.
                if (!rejected && frame.lastFullFrameSentTick > 0 && frame.lastFullFrameSentTick >= failedTick &&
                    input.ackedServerTick >= frame.lastFullFrameSentTick)
                {
                    TraceHistoryResync("Covered", player, failedTick, frame.reliableFrame.pendingTick,
                        input.ackedServerTick, frame.lastFullFrameSentTick);
                    return;
                }

                // Wait for delivery proof before replacing an in-flight checkpoint.
                if (frame.reliableFrame.IsPending(input.ackedServerTick))
                {
                    if (failedTick > frame.lastFullFrameSentTick)
                        frame.requiresFullCheckpoint = true;
                    _clientFrames[i] = frame;
                    TraceHistoryResync("Coalesced", player, failedTick, frame.reliableFrame.pendingTick,
                        input.ackedServerTick, frame.reliableSentAtLocalTick);
                    return;
                }
                frame.ClearRecoveryFrame();
                _clientFrames[i] = frame;
                if (frame.requiresFullCheckpoint || frame.fullFrame)
                {
                    TraceHistoryResync("Coalesced", player, failedTick, 0, input.ackedServerTick);
                    return;
                }

                double now = Time.unscaledTimeAsDouble;
                if (_historyResyncServedAt.TryGetValue(player, out double last) &&
                    now - last < HistoryResyncRetrySeconds)
                    return;

                if (QueueFullResync(player))
                {
                    _historyResyncServedAt[player] = now;
                    TraceHistoryResync("Serve", player, failedTick, frame.reliableFrame.pendingTick,
                        input.ackedServerTick);
                }
                return;
            }
        }
    }
}
