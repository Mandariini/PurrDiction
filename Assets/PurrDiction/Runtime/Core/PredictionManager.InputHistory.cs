using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        internal readonly struct CachedInputEntry
        {
            public readonly PredictedComponentID id;
            public readonly PredictedObjectID rootId;
            public readonly int bitOrigin;
            public readonly int bitLength;
            // The previous tick contains identical bits for this ID under the same root.
            public readonly bool repeatsPrevious;

            public CachedInputEntry(
                PredictedComponentID id,
                PredictedObjectID rootId,
                int bitOrigin,
                int bitLength,
                bool repeatsPrevious)
            {
                this.id = id;
                this.rootId = rootId;
                this.bitOrigin = bitOrigin;
                this.bitLength = bitLength;
                this.repeatsPrevious = repeatsPrevious;
            }
        }

        // framedPacker assumes the receiver has the preceding tick; the first transcript tick must be full.
        private struct CachedInputBlock
        {
            public ulong tick;
            public bool captured;
            public BitPacker packer;
            public List<CachedInputEntry> entries;
            public BitPacker framedPacker;
        }

        private CachedInputBlock[] _inputBlockCache;
        private CachedInputBlock _inputBlockScratch;
        private readonly Dictionary<PredictedComponentID, int> _previousInputEntryIndex = new();
        private ulong _latestCapturedInputTick;
        private bool _hasCapturedInputHistory;

        // Capture after PrepareInput, even without observers; later roster changes cannot reconstruct this tick.
        private void CaptureInputHistory(ulong tick)
        {
            EnsureInputHistoryCapacity();
            if (_hasCapturedInputHistory && tick <= _latestCapturedInputTick)
            {
                if (TryGetInputBlockForTick(tick, out _))
                    return;
                throw new InvalidOperationException(
                    $"Cannot capture authoritative input tick {tick} after tick {_latestCapturedInputTick}.");
            }

            ref var scratch = ref _inputBlockScratch;
            scratch.packer ??= BitPackerPool.Get();
            scratch.framedPacker ??= BitPackerPool.Get();
            scratch.entries ??= new List<CachedInputEntry>();
            scratch.captured = false;
            scratch.packer.ResetPositionAndMode(false);
            scratch.framedPacker.ResetPositionAndMode(false);
            scratch.entries.Clear();

            _previousInputEntryIndex.Clear();
            CachedInputBlock previous = default;
            bool hasPrevious = tick > 0 && TryGetInputBlockForTick(tick - 1, out previous);
            if (hasPrevious)
            {
                for (var i = 0; i < previous.entries.Count; i++)
                    _previousInputEntryIndex[previous.entries[i].id] = i;
            }

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.hasInput)
                    continue;
                if (!system.HasInputAt(tick))
                    throw new MissingPredictionBaselineException(
                        $"Authoritative input {system.id} was not prepared at tick {tick}.");

                var id = system.id;
                var rootId = system.rootObjectId;
                int origin = scratch.packer.positionInBits;
                system.WriteFirstInput(tick, scratch.packer);
                int length = scratch.packer.positionInBits - origin;

                bool repeats = false;
                if (hasPrevious && _previousInputEntryIndex.TryGetValue(id, out int previousIndex))
                {
                    var previousEntry = previous.entries[previousIndex];
                    repeats = previousEntry.rootId.Equals(rootId) &&
                              new BitData(scratch.packer, origin, length).Equals(
                                  new BitData(previous.packer, previousEntry.bitOrigin, previousEntry.bitLength));
                }

                scratch.entries.Add(new CachedInputEntry(id, rootId, origin, length, repeats));
            }

            var framed = scratch.framedPacker;
            Packer<PackedUInt>.Write(framed, (uint)scratch.entries.Count);
            for (var i = 0; i < scratch.entries.Count; i++)
                WriteTranscriptEntry(framed, in scratch, i, scratch.entries[i].repeatsPrevious);

            // Publish only after user packers succeed, preserving retained history if one throws.
            int index = (int)(tick % (ulong)_inputBlockCache.Length);
            var replaced = _inputBlockCache[index];
            scratch.tick = tick;
            scratch.captured = true;
            _inputBlockCache[index] = scratch;
            _inputBlockScratch = replaced;
            _inputBlockScratch.captured = false;

            // Tick jumps can leave expired ring slots that were not overwritten.
            if (_hasCapturedInputHistory && tick - _latestCapturedInputTick > 1)
                PruneInputHistory(tick);
            _latestCapturedInputTick = tick;
            _hasCapturedInputHistory = true;
        }

        private static void WriteTranscriptEntry(BitPacker frame, in CachedInputBlock block, int index, bool repeat)
        {
            var entry = block.entries[index];
            Packer<PredictedComponentID>.Write(frame, entry.id);
            Packer<bool>.Write(frame, repeat);
            if (repeat)
                return;
            Packer<PackedUInt>.Write(frame, (uint)entry.bitLength);
            frame.WriteBitDataWithoutConsumingIt(new BitData(block.packer, entry.bitOrigin, entry.bitLength));
        }

        private void EnsureInputHistoryCapacity()
        {
            int capacity = checked((int)verifiedHistoryWindowTicks + 1);
            if (_inputBlockCache != null && _inputBlockCache.Length == capacity)
                return;

            var previous = _inputBlockCache;
            _inputBlockCache = new CachedInputBlock[capacity];
            if (previous == null)
                return;

            for (var i = 0; i < previous.Length; i++)
            {
                ref var block = ref previous[i];
                if (block.captured && _hasCapturedInputHistory &&
                    block.tick <= _latestCapturedInputTick &&
                    _latestCapturedInputTick - block.tick <= verifiedHistoryWindowTicks)
                {
                    _inputBlockCache[(int)(block.tick % (ulong)capacity)] = block;
                    block = default;
                }
                else
                {
                    DisposeInputBlock(ref block);
                }
            }

            // Release the spare buffer capacity retained from the previous tick rate.
            DisposeInputBlock(ref _inputBlockScratch);
        }

        private void PruneInputHistory(ulong throughTick)
        {
            for (var i = 0; i < _inputBlockCache.Length; i++)
            {
                ref var block = ref _inputBlockCache[i];
                if (block.captured &&
                    (block.tick > throughTick || throughTick - block.tick > verifiedHistoryWindowTicks))
                    DisposeInputBlock(ref block);
            }
        }

        private bool TryGetInputBlockForTick(ulong tick, out CachedInputBlock block)
        {
            EnsureInputHistoryCapacity();
            if (_hasCapturedInputHistory && tick <= _latestCapturedInputTick &&
                _latestCapturedInputTick - tick <= verifiedHistoryWindowTicks)
            {
                block = _inputBlockCache[(int)(tick % (ulong)_inputBlockCache.Length)];
                if (block.captured && block.tick == tick)
                    return true;
            }

            block = default;
            return false;
        }

        private CachedInputBlock GetInputBlockForTick(ulong tick)
        {
            if (TryGetInputBlockForTick(tick, out var block))
                return block;
            throw new MissingPredictionBaselineException(
                $"Authoritative input tick {tick} was not captured or is no longer retained.");
        }

        private static void DisposeInputBlock(ref CachedInputBlock block)
        {
            block.packer?.Dispose();
            block.framedPacker?.Dispose();
            block = default;
        }

        private void DisposeInputBlockCache()
        {
            if (_inputBlockCache != null)
            {
                for (var i = 0; i < _inputBlockCache.Length; i++)
                    DisposeInputBlock(ref _inputBlockCache[i]);
                _inputBlockCache = null;
            }

            DisposeInputBlock(ref _inputBlockScratch);
            _latestCapturedInputTick = 0;
            _hasCapturedInputHistory = false;
        }
    }
}
