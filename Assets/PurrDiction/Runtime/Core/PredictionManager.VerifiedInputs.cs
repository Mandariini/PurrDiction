using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private struct VerifiedInputTick
        {
            public int firstEntry;
            public int count;
        }

        private readonly List<VerifiedInputTick> _verifiedInputTicks = new();
        private readonly List<InputHistorySpan> _verifiedInputEntries = new();
        private readonly HashSet<PredictedComponentID> _verifiedInputIds = new();
        private readonly Dictionary<PredictedComponentID, int> _previousVerifiedInputIndex = new();
        private BitPacker _verifiedInputPayload;
        private ulong _verifiedInputFrom;
        private ulong _verifiedInputThrough;
        private bool _requiresVerifiedInputCheckpoint;

        private void ClearVerifiedInputTranscript()
        {
            _verifiedInputPayload?.Dispose();
            _verifiedInputPayload = null;
            _verifiedInputTicks.Clear();
            _verifiedInputEntries.Clear();
            _verifiedInputIds.Clear();
            _previousVerifiedInputIndex.Clear();
        }

        private void BeginVerifiedInputTranscript(ulong firstTick, ulong lastTick)
        {
            ClearVerifiedInputTranscript();
            _verifiedInputPayload = BitPackerPool.Get();
            _verifiedInputFrom = firstTick;
            _verifiedInputThrough = lastTick;
        }

        private void BeginVerifiedInputTick()
        {
            _verifiedInputIds.Clear();
            _verifiedInputTicks.Add(new VerifiedInputTick { firstEntry = _verifiedInputEntries.Count });
        }

        private void StageVerifiedInput(
            ulong tick, PredictedComponentID id, BitPacker source, int origin, int length)
        {
            int destinationOrigin = _verifiedInputPayload.positionInBits;
            _verifiedInputPayload.WriteBitDataWithoutConsumingIt(new BitData(source, origin, length));
            StageVerifiedInputSpan(tick, id, destinationOrigin, length);
        }

        private void StageRepeatedVerifiedInput(ulong tick, PredictedComponentID id)
        {
            if (!_previousVerifiedInputIndex.TryGetValue(id, out int previousIndex))
                throw new MissingPredictionBaselineException(
                    $"Authoritative input for {id} at tick {tick} repeats an entry the previous tick did not carry.");
            var previous = _verifiedInputEntries[previousIndex];
            StageVerifiedInputSpan(tick, id, previous.bitOrigin, previous.bitLength);
        }

        private void StageVerifiedInputSpan(ulong tick, PredictedComponentID id, int origin, int length)
        {
            if (!_verifiedInputIds.Add(id))
                throw new MissingPredictionBaselineException($"Duplicate authoritative input for {id} at tick {tick}.");
            _verifiedInputEntries.Add(new InputHistorySpan
            {
                id = id,
                bitOrigin = origin,
                bitLength = length
            });
            int index = _verifiedInputTicks.Count - 1;
            var batch = _verifiedInputTicks[index];
            batch.count++;
            _verifiedInputTicks[index] = batch;
        }

        private void EndVerifiedInputTick()
        {
            _previousVerifiedInputIndex.Clear();
            var batch = _verifiedInputTicks[_verifiedInputTicks.Count - 1];
            for (int i = batch.firstEntry; i < batch.firstEntry + batch.count; i++)
                _previousVerifiedInputIndex[_verifiedInputEntries[i].id] = i;
        }

        private void ReadInputHistory(BitPacker frame, ulong serverTick, ulong baselineTick, int frameEndBit)
        {
            using var marker = ReadInputHistoryMarker.Auto();
            if (baselineTick > serverTick || serverTick - baselineTick > verifiedHistoryWindowTicks ||
                frame.positionInBits >= frameEndBit)
                throw new MissingPredictionBaselineException("Invalid authoritative input history window.");

            uint count = Packer<PackedUInt>.Read(frame);
            if (frame.positionInBits > frameEndBit || count != serverTick - baselineTick)
                throw new MissingPredictionBaselineException(
                    $"Authoritative inputs must cover every tick after {baselineTick} through {serverTick}.");

            BeginVerifiedInputTranscript(baselineTick + 1, serverTick);
            for (uint k = 0; k < count; k++)
            {
                ulong tick = baselineTick + 1 + k;
                if (frame.positionInBits >= frameEndBit)
                    throw new MissingPredictionBaselineException($"Missing authoritative input block at tick {tick}.");
                uint entries = Packer<PackedUInt>.Read(frame);
                if (frame.positionInBits > frameEndBit || entries > (uint)(frameEndBit - frame.positionInBits))
                    throw new MissingPredictionBaselineException($"Invalid authoritative input count at tick {tick}.");
                BeginVerifiedInputTick();
                for (uint e = 0; e < entries; e++)
                {
                    var id = Packer<PredictedComponentID>.Read(frame);
                    bool repeat = Packer<bool>.Read(frame);
                    if (frame.positionInBits > frameEndBit)
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    if (repeat)
                    {
                        if (k == 0)
                            throw new MissingPredictionBaselineException(
                                $"Authoritative input for {id} at tick {tick} repeats before the transcript's first tick.");
                        StageRepeatedVerifiedInput(tick, id);
                        continue;
                    }
                    uint bits = Packer<PackedUInt>.Read(frame);
                    int origin = frame.positionInBits;
                    if (origin > frameEndBit || bits == 0 || bits > (uint)(frameEndBit - origin))
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    frame.SkipBits(checked((int)bits));
                    StageVerifiedInput(tick, id, frame, origin, (int)bits);
                }
                EndVerifiedInputTick();
            }
        }

        // Decode after rollback has recreated identities; predicted history does not prove receipt.
        private void ApplyVerifiedInputs(ulong tick)
        {
            if (_verifiedInputPayload == null || tick < _verifiedInputFrom || tick > _verifiedInputThrough ||
                tick - _verifiedInputFrom >= (ulong)_verifiedInputTicks.Count)
                throw new MissingPredictionBaselineException($"Missing authoritative input transcript at tick {tick}.");

            var batch = _verifiedInputTicks[(int)(tick - _verifiedInputFrom)];
            _verifiedInputIds.Clear();
            using var payload = BitPackerPool.Get();
            for (int i = batch.firstEntry; i < batch.firstEntry + batch.count; i++)
            {
                var entry = _verifiedInputEntries[i];
                if (!_instanceMap.TryGetValue(entry.id, out var system) || !system || !system.hasInput)
                    throw new MissingPredictionBaselineException(
                        $"Cannot resolve authoritative input identity {entry.id} at tick {tick}.");
                payload.ResetPositionAndMode(false);
                payload.WriteBitDataWithoutConsumingIt(
                    new BitData(_verifiedInputPayload, entry.bitOrigin, entry.bitLength));
                payload.ResetPositionAndMode(true);
                try
                {
                    system.ReadFirstInput(tick, payload);
                }
                catch (Exception error)
                {
                    throw new MissingPredictionBaselineException(
                        $"Cannot read authoritative input for {entry.id} at tick {tick}: {error.Message}");
                }
                if (payload.positionInBits != entry.bitLength || !system.HasInputAt(tick))
                    throw new MissingPredictionBaselineException(
                        $"Incomplete authoritative input for {entry.id} at tick {tick}.");
                _verifiedInputIds.Add(entry.id);
            }

            for (int i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.hasInput && !system.SkipsCurrentSimulationPhase() && !_verifiedInputIds.Contains(system.id))
                    throw new MissingPredictionBaselineException(
                        $"Missing authoritative input for {system.id} at tick {tick}.");
            }
        }
    }
}
