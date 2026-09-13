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
        private readonly List<bool> _verifiedInputRepeatScratch = new();
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
            _verifiedInputRepeatScratch.Clear();
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

        private void StageRepeatedVerifiedInput(ulong tick, in InputHistorySpan previous)
            => StageVerifiedInputSpan(tick, previous.id, previous.bitOrigin, previous.bitLength);

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
                if (entries == 0)
                    continue;

                var previousBatch = k > 0 ? _verifiedInputTicks[(int)k - 1] : default;
                bool sameRoster = k > 0 && Packer<bool>.Read(frame);
                _verifiedInputRepeatScratch.Clear();
                if (sameRoster)
                {
                    if ((uint)previousBatch.count != entries || entries > (uint)(frameEndBit - frame.positionInBits))
                        throw new MissingPredictionBaselineException(
                            $"Authoritative input roster at tick {tick} does not match the previous tick it repeats.");
                    for (uint e = 0; e < entries; e++)
                        _verifiedInputRepeatScratch.Add(Packer<bool>.Read(frame));
                }
                SkipTranscriptPadding(frame, frameEndBit, tick);

                for (int e = 0; e < (int)entries; e++)
                {
                    if (sameRoster && _verifiedInputRepeatScratch[e])
                    {
                        StageRepeatedVerifiedInput(tick, _verifiedInputEntries[previousBatch.firstEntry + e]);
                        continue;
                    }
                    var id = Packer<PredictedComponentID>.Read(frame);
                    if (frame.positionInBits > frameEndBit)
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    if (sameRoster && !_verifiedInputEntries[previousBatch.firstEntry + e].id.Equals(id))
                        throw new MissingPredictionBaselineException(
                            $"Authoritative input roster at tick {tick} does not match the previous tick it repeats.");
                    uint bits = Packer<PackedUInt>.Read(frame);
                    int origin = frame.positionInBits;
                    if (origin > frameEndBit || bits == 0 || bits > (uint)(frameEndBit - origin))
                        throw new MissingPredictionBaselineException($"Truncated authoritative input for {id} at tick {tick}.");
                    frame.SkipBits(checked((int)bits));
                    StageVerifiedInput(tick, id, frame, origin, (int)bits);
                }
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
