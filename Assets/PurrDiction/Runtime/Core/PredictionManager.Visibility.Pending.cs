using System.Collections.Generic;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        readonly Dictionary<PlayerID, PlayerPendingVisibilityDeletes> _pendingVisibilityDeletes = new ();
        readonly List<InstanceDetails> _pendingVisibilityRecordScratch = new ();

        internal void CapturePendingVisibilityDelete(PredictedObjectID objectId)
        {
            if (!hierarchy)
                return;

            _pendingVisibilityRecordScratch.Clear();
            if (!hierarchy.TryCollectVisibilityDeleteSnapshot(
                    objectId,
                    _pendingVisibilityRecordScratch,
                    out var rootId))
            {
                return;
            }

            foreach (var pair in _playerVisibility)
            {
                bool receiverHasRoot = pair.Value.IsVisible(rootId);
                if (!receiverHasRoot &&
                    _clientTicks.TryGetValue(pair.Key, out var queue))
                {
                    receiverHasRoot = pair.Value.WasVisibleAt(
                        rootId,
                        queue.ackedServerTick);
                }

                if (!receiverHasRoot)
                    continue;

                if (!_pendingVisibilityDeletes.TryGetValue(pair.Key, out var receiverPending))
                {
                    receiverPending = new PlayerPendingVisibilityDeletes();
                    _pendingVisibilityDeletes.Add(pair.Key, receiverPending);
                }

                receiverPending.Capture(
                    objectId,
                    rootId,
                    _pendingVisibilityRecordScratch,
                    localTick);
            }
        }

        void AcknowledgePendingVisibilityDeletes(
            PlayerID player,
            ulong acknowledgedTick)
        {
            if (!_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                return;

            pending.Acknowledge(acknowledgedTick);
            if (pending.Count == 0)
                _pendingVisibilityDeletes.Remove(player);
        }

        void PreparePendingVisibilityDeletesCurrent(
            PlayerID player,
            ulong tick,
            ref PredictedHierarchyState projection)
        {
            if (_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                pending.PrepareCurrent(tick, ref projection);
        }

        void PreparePendingVisibilityDeletesBaseline(
            PlayerID player,
            ulong baselineTick,
            ref PredictedHierarchyState projection)
        {
            if (_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                pending.PrepareBaseline(baselineTick, ref projection);
        }

        bool RequiresFullVisibilityDeleteFrame(PlayerID player, ulong tick)
        {
            return _pendingVisibilityDeletes.TryGetValue(player, out var pending) &&
                   pending.RequiresFullFrame(tick);
        }

        bool HasPendingVisibilityDeletes(PlayerID player)
        {
            return _pendingVisibilityDeletes.TryGetValue(player, out var pending) &&
                   pending.Count > 0;
        }

        void MarkPendingVisibilityDeletesSent(PlayerID player, ulong tick)
        {
            if (_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                pending.MarkSent(tick);
        }

        void RemovePendingVisibilityDeletes(PlayerID player)
        {
            if (_pendingVisibilityDeletes.Remove(player, out var pending))
                pending.Clear();
        }

        void ClearPendingVisibilityDeletes()
        {
            foreach (var pending in _pendingVisibilityDeletes.Values)
                pending.Clear();
            _pendingVisibilityDeletes.Clear();
            _pendingVisibilityRecordScratch.Clear();
        }
    }
}
