using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    // Component IDs decouple records from local system order; bit lengths let receivers
    // skip unknown records without byte-aligning their payloads.
    internal static class AddressedPredictionRecords
    {
        private static readonly WriteFunc<bool> StandardBoolWriter = PackIntegers.Write;
        private static readonly WriteFunc<PackedUInt> StandardUIntWriter = PackingIntegers.Write;
        private static WriteFunc<bool> _lastBoolWriter;
        private static WriteFunc<PackedUInt> _lastUIntWriter;
        private static WriteFunc<PredictedObjectID> _lastObjectWriter;
        private static WriteFunc<PredictedComponentID> _lastComponentWriter;
        private static bool _checkedHeaderWriters, _standardHeaderWriters;

        // Standard headers depend only on their values, so cached records can move between
        // bit origins. Custom codecs retain the normal path, including position-aware codecs.
        internal static bool UsesStandardHeaders()
        {
            var boolWriter = Packer<bool>.WriteFunc;
            var uintWriter = Packer<PackedUInt>.WriteFunc;
            var objectWriter = Packer<PredictedObjectID>.WriteFunc;
            var componentWriter = Packer<PredictedComponentID>.WriteFunc;
            if (_checkedHeaderWriters && ReferenceEquals(boolWriter, _lastBoolWriter) &&
                ReferenceEquals(uintWriter, _lastUIntWriter) && ReferenceEquals(objectWriter, _lastObjectWriter) &&
                ReferenceEquals(componentWriter, _lastComponentWriter))
                return _standardHeaderWriters;
            _checkedHeaderWriters = true;
            _lastBoolWriter = boolWriter;
            _lastUIntWriter = uintWriter;
            _lastObjectWriter = objectWriter;
            _lastComponentWriter = componentWriter;
            _standardHeaderWriters = StandardBoolWriter.Equals(boolWriter) && StandardUIntWriter.Equals(uintWriter) &&
                IsGeneratedIdWriter(objectWriter, "PurrNet_Prediction_PredictedObjectID_Serializer") &&
                IsGeneratedIdWriter(componentWriter, "PurrNet_Prediction_PredictedComponentID_Serializer");
            return _standardHeaderWriters;
        }

        private static bool IsGeneratedIdWriter(Delegate writer, string serializerName)
        {
            var type = writer?.Method.DeclaringType;
            return writer?.Target == null && type != null &&
                type.Assembly == typeof(PredictedComponentID).Assembly && type.Name == serializerName &&
                type.Namespace == "PurrNet.Prediction.PurrNet.CodeGen.Serializers" && writer.Method.Name == "Write" &&
                writer.GetInvocationList().Length == 1;
        }

        internal delegate void ReadRecord(
            PredictedComponentID id,
            bool isFullState,
            BitPacker payload,
            int payloadBitCount);

        // The source is already past the failed record, so callers can continue the section.
        internal delegate void RecordFailure(
            PredictedComponentID id,
            Exception error,
            int declaredBits,
            int consumedBits);

        // A leading BitPacker would make codegen discover this as a global serializer.
        internal static void WriteSectionCount(int count, BitPacker packer)
        {
            Packer<PackedUInt>.Write(packer, (uint)count);
        }

        internal static void CompleteSection(int count, int sectionOrigin, int recordsOrigin, BitPacker destination)
        {
            using var header = BitPackerPool.Get();
            header.SetBitPosition(sectionOrigin);
            WriteSectionCount(count, header);
            ReplaceSectionHeader(sectionOrigin, recordsOrigin, header, destination);
        }

        // Records already occupy the frame. Usually the count has the reserved width,
        // so completing the section touches only its header. Varints change by whole
        // bytes; overlap-safe byte moves handle those width changes without bit shifting.
        internal static void ReplaceSectionHeader(int sectionOrigin, int recordsOrigin,
            BitPacker header, BitPacker destination)
        {
            int oldEnd = destination.positionInBits;
            int headerBits = header.positionInBits - sectionOrigin;
            int newRecordsOrigin = checked(sectionOrigin + headerBits);
            int payloadBits = oldEnd - recordsOrigin;
            int shift = newRecordsOrigin - recordsOrigin;
            int newEnd = checked(oldEnd + shift);

            if (shift != 0 && payloadBits != 0)
            {
                if ((shift & 7) == 0)
                {
                    if (shift > 0)
                        destination.EnsureBitsExist(shift);
                    var bytes = destination.buffer;
                    int offset = recordsOrigin & 7;
                    int length = (offset + payloadBits + 7) >> 3;
                    int firstByte = newRecordsOrigin >> 3;
                    int prefixMask = (1 << offset) - 1;
                    byte prefix = (byte)(bytes[firstByte] & prefixMask);
                    Array.Copy(bytes, recordsOrigin >> 3, bytes, firstByte, length);
                    bytes[firstByte] = (byte)((bytes[firstByte] & ~prefixMask) | prefix);
                }
                else
                {
                    // Preserve arbitrary custom header codecs as well as byte varints.
                    using var records = BitPackerPool.Get();
                    records.WriteBitDataWithoutConsumingIt(new BitData(destination, recordsOrigin, payloadBits));
                    destination.SetBitPosition(newRecordsOrigin);
                    destination.WriteBitsWithoutConsumingIt(records, payloadBits);
                }
            }

            destination.SetBitPosition(sectionOrigin);
            destination.WriteBitDataWithoutConsumingIt(new BitData(header, sectionOrigin, headerBits));
            destination.SetBitPosition(newEnd);
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

        // Keep the codec body at its original bit origin while avoiding a concatenation copy.
        internal static void WritePrefixedRecord(
            PredictedComponentID id,
            bool isFullState,
            BitPacker prefix,
            BitPacker body,
            BitPacker destination)
        {
            int prefixBits = prefix.positionInBits;
            int bodyBits = body.positionInBits;
            Packer<PredictedComponentID>.Write(destination, id);
            Packer<bool>.Write(destination, isFullState);
            Packer<PackedUInt>.Write(destination, checked((uint)(prefixBits + bodyBits)));
            destination.WriteBitsWithoutConsumingIt(prefix, prefixBits);
            destination.WriteBitsWithoutConsumingIt(body, bodyBits);
        }

        internal static void ReadSection(ReadRecord readRecord, BitPacker source, RecordFailure onRecordFailure = null)
        {
            PackedUInt count = default;
            Packer<PackedUInt>.Read(source, ref count);

            for (uint i = 0; i < count.value; i++)
                ReadOne(readRecord, source, onRecordFailure);
        }

        // Gap replay needs the trailing event transcript before ordinary states are decoded.
        internal static void SkipSection(BitPacker source, int endBit, uint maximumRecords = uint.MaxValue)
        {
            if (source.positionInBits >= endBit)
                throw new InvalidOperationException("Missing addressed section header.");
            uint count = Packer<PackedUInt>.Read(source);
            if (source.positionInBits > endBit || count > maximumRecords ||
                count > (uint)(endBit - source.positionInBits))
                throw new InvalidOperationException("Addressed section count exceeds its frame.");
            for (uint i = 0; i < count; i++)
            {
                Packer<PredictedComponentID>.Read(source);
                Packer<bool>.Read(source);
                uint bits = Packer<PackedUInt>.Read(source);
                int next = checked(source.positionInBits + checked((int)bits));
                if (next > endBit)
                    throw new InvalidOperationException("Addressed record exceeds its frame.");
                source.SetBitPosition(next);
            }
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
