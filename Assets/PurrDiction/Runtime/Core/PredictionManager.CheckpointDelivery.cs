using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        internal const int MaxStagedCheckpointBytes = 1024 * 1024;
        internal const double MaxStagedCheckpointAgeSeconds = 2d;

        private ulong _appliedCheckpointTick;
        private ulong _waitingCheckpointTick;
        private ulong _queuedCheckpointTick;
        private ulong _rejectedCheckpointTick;
        private ulong _checkpointRepairAfterTick;
        private ulong _latestCheckpointContinuationTick;
        private FrameDelta? _stagedCheckpointFrame;
        private double _stagedCheckpointReceivedAt;

        public int stagedCheckpointFrames => _stagedCheckpointFrame.HasValue ? 1 : 0;
        public int stagedCheckpointBytes => _stagedCheckpointFrame.HasValue
            ? _stagedCheckpointFrame.Value.packer.ToByteData().length
            : 0;

        public ulong freshFramesSentTotal { get; private set; }
        public ulong pipelinedDeltaFramesSentTotal { get; private set; }
        public ulong oversizedFramesDeferredTotal { get; private set; }

        private void ClearCheckpointDelivery()
        {
            DisposeStagedCheckpoint();
            _appliedCheckpointTick = 0;
            _waitingCheckpointTick = 0;
            _queuedCheckpointTick = 0;
            _rejectedCheckpointTick = 0;
            _checkpointRepairAfterTick = 0;
            _latestCheckpointContinuationTick = 0;
            _stagedCheckpointReceivedAt = 0d;
            _consecutiveRejectedCheckpoints = 0;
            _tolerateReplayHookFailures = false;
            _toleratedReplayHookFailures = 0;
            freshFramesSentTotal = 0;
            pipelinedDeltaFramesSentTotal = 0;
            oversizedFramesDeferredTotal = 0;
        }

        // Takes ownership of the decoded payload, including disposal of dropped or replaced frames.
        private void ReceiveCheckpointFrame(FrameDelta frame)
        {
            ulong checkpoint = frame.checkpointTick;
            if (checkpoint == 0 || checkpoint > frame.serverTick ||
                frame.serverTick <= _verifiedServerTick ||
                checkpoint < _appliedCheckpointTick ||
                checkpoint < _waitingCheckpointTick ||
                checkpoint <= _rejectedCheckpointTick)
            {
                frame.Dispose();
                return;
            }

            if (frame.fullFrame)
            {
                if (checkpoint != frame.serverTick || checkpoint <= _appliedCheckpointTick ||
                    checkpoint == _queuedCheckpointTick)
                {
                    frame.Dispose();
                    return;
                }

                BeginWaitingForCheckpoint(checkpoint);

                // Continuations stay staged until their checkpoint applies successfully.
                while (_deltas.Count > 0)
                    _deltas.Dequeue().Dispose();

                _queuedCheckpointTick = checkpoint;
                _deltas.Enqueue(frame);
                return;
            }

            if (frame.serverTick <= checkpoint || frame.baselineTick < checkpoint ||
                frame.baselineTick > frame.serverTick)
            {
                if (checkpoint > _appliedCheckpointTick)
                {
                    BeginWaitingForCheckpoint(checkpoint);
                    DeferCheckpointRepair(frame.serverTick);
                }
                else
                    MarkHistoryResyncNeeded(frame.serverTick);
                frame.Dispose();
                return;
            }

            if (checkpoint == _appliedCheckpointTick)
            {
                // Receipt alone must not advance ACKs; replay must succeed first.
                _deltas.Enqueue(frame);
                return;
            }

            BeginWaitingForCheckpoint(checkpoint);
            if (frame.serverTick <= _latestCheckpointContinuationTick)
            {
                frame.Dispose();
                return;
            }

            _latestCheckpointContinuationTick = frame.serverTick;
            ExpireStagedCheckpoint();

            if (_checkpointRepairAfterTick > 0 ||
                frame.packer.ToByteData().length > MaxStagedCheckpointBytes ||
                frame.serverTick - checkpoint > verifiedHistoryWindowTicks)
            {
                DeferCheckpointRepair(frame.serverTick);
                frame.Dispose();
                return;
            }

            DisposeStagedCheckpoint();
            _stagedCheckpointFrame = frame;
        }

        private void BeginWaitingForCheckpoint(ulong checkpoint)
        {
            if (checkpoint <= _waitingCheckpointTick)
                return;

            DisposeStagedCheckpoint();
            int queued = _deltas.Count;
            for (int i = 0; i < queued; i++)
            {
                var frame = _deltas.Dequeue();
                if (frame.checkpointTick < checkpoint)
                    frame.Dispose();
                else
                    _deltas.Enqueue(frame);
            }
            _waitingCheckpointTick = checkpoint;
            _queuedCheckpointTick = 0;
            _checkpointRepairAfterTick = 0;
            _latestCheckpointContinuationTick = 0;
            _stagedCheckpointReceivedAt = Time.unscaledTimeAsDouble;
        }

        private void DeferCheckpointRepair(ulong failedTick)
        {
            _checkpointRepairAfterTick = Math.Max(_checkpointRepairAfterTick, failedTick);
            _latestCheckpointContinuationTick = Math.Max(_latestCheckpointContinuationTick, failedTick);
            DisposeStagedCheckpoint();
        }

        private void ExpireStagedCheckpoint()
        {
            if (_waitingCheckpointTick == 0 || _latestCheckpointContinuationTick == 0 ||
                Time.unscaledTimeAsDouble - _stagedCheckpointReceivedAt < MaxStagedCheckpointAgeSeconds)
                return;

            // Keep the checkpoint promise until delivery is proven, even after releasing its bytes.
            DeferCheckpointRepair(_latestCheckpointContinuationTick);
        }

        private void RejectCheckpoint(ulong checkpoint)
        {
            _rejectedCheckpointTick = Math.Max(_rejectedCheckpointTick, checkpoint);
            ulong repairAfter = Math.Max(checkpoint, _checkpointRepairAfterTick);

            if (_waitingCheckpointTick <= checkpoint)
                ClearWaitingCheckpoint();

            int queued = _deltas.Count;
            for (int i = 0; i < queued; i++)
            {
                var frame = _deltas.Dequeue();
                if (frame.checkpointTick <= checkpoint)
                    frame.Dispose();
                else
                    _deltas.Enqueue(frame);
            }

            // A failed full payload proves delivery, allowing recovery to release the server slot.
            MarkHistoryResyncNeeded(repairAfter);
        }

        private void ReleaseCheckpointContinuation(ulong checkpoint)
        {
            if (_waitingCheckpointTick != checkpoint)
                return;

            ExpireStagedCheckpoint();
            ulong repairAfter = _checkpointRepairAfterTick;
            var continuation = _stagedCheckpointFrame;
            _stagedCheckpointFrame = null;
            ClearWaitingCheckpoint();

            if (continuation.HasValue)
                _deltas.Enqueue(continuation.Value);

            if (repairAfter > checkpoint)
                MarkHistoryResyncNeeded(repairAfter);
        }

        private void ClearWaitingCheckpoint()
        {
            DisposeStagedCheckpoint();
            _waitingCheckpointTick = 0;
            _queuedCheckpointTick = 0;
            _checkpointRepairAfterTick = 0;
            _latestCheckpointContinuationTick = 0;
            _stagedCheckpointReceivedAt = 0d;
        }

        private void DisposeStagedCheckpoint()
        {
            if (!_stagedCheckpointFrame.HasValue)
                return;

            _stagedCheckpointFrame.Value.Dispose();
            _stagedCheckpointFrame = null;
        }
    }
}
