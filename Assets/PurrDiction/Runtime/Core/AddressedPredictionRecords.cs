using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Length-delimited prediction records. The component id makes each record independent
    /// of the receiver's local system order; the bit length makes unknown records skippable
    /// without byte-aligning their payload.
    /// </summary>
    internal static class AddressedPredictionRecords
    {
        internal delegate void ReadRecord(
            PredictedComponentID id,
            bool isFullState,
            BitPacker payload,
            int payloadBitCount);

        /// <summary>
        /// Invoked instead of throwing when a record's payload fails to decode. The source
        /// stream is already positioned past the record, so the caller may keep reading the
        /// remaining records of the section.
        /// </summary>
        internal delegate void RecordFailure(
            PredictedComponentID id,
            Exception error,
            int declaredBits,
            int consumedBits);

        // Keep BitPacker out of the first parameter. PurrNet codegen treats every static
        // (BitPacker, T) method as a global serializer, regardless of its name or visibility.
        internal static void WriteSectionCount(int count, BitPacker packer)
        {
            Packer<PackedUInt>.Write(packer, (uint)count);
        }


        public static void WriteRecord(
            BitPacker destination,
            PredictedComponentID id,
            bool isFullState,
            BitPacker payload)
        {
            int payloadBits = payload.positionInBits;
            Packer<PredictedComponentID>.Write(destination, id);
            Packer<bool>.Write(destination, isFullState);
            Packer<PackedUInt>.Write(destination, (uint)payloadBits);
            destination.WriteBitsWithoutConsumingIt(payload, payloadBits);
        }

        internal static void ReadSection(ReadRecord readRecord, BitPacker source, RecordFailure onRecordFailure = null)
        {
            PackedUInt count = default;
            Packer<PackedUInt>.Read(source, ref count);

            for (uint i = 0; i < count.value; i++)
                ReadOne(readRecord, source, onRecordFailure);
        }

        internal static void ReadOne(ReadRecord readRecord, BitPacker source, RecordFailure onRecordFailure = null)
        {
            PredictedComponentID id = default;
            Packer<PredictedComponentID>.Read(source, ref id);

            bool isFullState = default;
            Packer<bool>.Read(source, ref isFullState);

            PackedUInt payloadBits = default;
            Packer<PackedUInt>.Read(source, ref payloadBits);

            int payloadLength = checked((int)payloadBits.value);
            using var boundedPayload = BitPackerPool.Get();
            boundedPayload.WriteBits(source, payloadLength);
            boundedPayload.ResetPositionAndMode(true);

            Exception failure = null;

            try
            {
                readRecord?.Invoke(id, isFullState, boundedPayload, payloadLength);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            int consumedBits = boundedPayload.positionInBits;

            if (failure == null && consumedBits != payloadLength && consumedBits != 0)
            {
                failure = new InvalidOperationException(
                    $"Prediction record {id} consumed {consumedBits} bits " +
                    $"of its declared {payloadLength}-bit payload.");
            }

            if (failure == null)
                return;

            if (onRecordFailure != null)
            {
                onRecordFailure(id, failure, payloadLength, consumedBits);
                return;
            }

            throw new InvalidOperationException(
                $"Failed to read prediction record {id}: {failure.Message}",
                failure);
        }
    }
}
