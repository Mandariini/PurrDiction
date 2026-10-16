using System;
using System.Runtime.CompilerServices;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal static class InputHistoryDelta
    {
        internal static bool TryWrite(BitPacker destination, in BitData previous, in BitData current)
        {
            int length = (int)previous.bitLength.value;
            if (length != (int)current.bitLength.value)
                return false;

            int rawBits = length + PrefixBits(length);
            int bytes = (length - 1) / 8 + 1;
            int encodedBits = ((bytes - 1) / 8 + 1) * 8;
            if (encodedBits >= rawBits)
                return false;
            if (length <= 512)
                return TryWriteBuffered(destination, previous, current, length, bytes, rawBits, encodedBits);

            for (int i = 0; i < bytes; i++)
            {
                if (PeekByte(previous, i, length) == PeekByte(current, i, length))
                    continue;
                encodedBits += 8;
                if (encodedBits >= rawBits)
                    return false;
            }

            for (int first = 0; first < bytes; first += 8)
            {
                int count = Math.Min(8, bytes - first);
                byte mask = 0;
                for (int i = 0; i < count; i++)
                {
                    if (PeekByte(previous, first + i, length) != PeekByte(current, first + i, length))
                        mask |= (byte)(1 << i);
                }
                destination.WriteBits(mask, 8);
                for (int i = 0; i < count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        destination.WriteBits(PeekByte(current, first + i, length), 8);
                }
            }
            return true;
        }

        private static bool TryWriteBuffered(BitPacker destination, in BitData previous, in BitData current,
            int length, int bytes, int rawBits, int encodedBits)
        {
            // Bound stack use for ordinary inputs. Compare eight bytes per word, then commit the
            // complete patch so an unhelpful delta leaves the destination untouched.
            Span<byte> encoded = stackalloc byte[72];
            int written = 0;
            var previousBuffer = previous.packer.buffer;
            var currentBuffer = current.packer.buffer;
            int previousOrigin = (int)previous.bitOrigin.value;
            int currentOrigin = (int)current.bitOrigin.value;
            for (int first = 0; first < bytes; first += 8)
            {
                int maskPosition = written++;
                byte mask = 0;
                int count = Math.Min(8, bytes - first);
                int bits = Math.Min(64, length - first * 8);
                ulong value = PeekWord(currentBuffer, currentOrigin + first * 8, bits);
                ulong changed = value ^ PeekWord(previousBuffer, previousOrigin + first * 8, bits);
                for (int i = 0; i < count; i++)
                {
                    if ((byte)(changed >> (i * 8)) == 0)
                        continue;
                    encodedBits += 8;
                    if (encodedBits >= rawBits)
                        return false;
                    mask |= (byte)(1 << i);
                    encoded[written++] = (byte)(value >> (i * 8));
                }
                encoded[maskPosition] = mask;
            }
            destination.WriteBytes(encoded.Slice(0, written));
            return true;
        }

        internal static void Read(BitPacker source, int sourceEndBit, in BitData previous,
            BitPacker destination)
        {
            int length = (int)previous.bitLength.value;
            int bytes = (length - 1) / 8 + 1;
            var previousBuffer = previous.packer.buffer;
            int previousOrigin = (int)previous.bitOrigin.value;
            for (int first = 0; first < bytes; first += 8)
            {
                int count = Math.Min(8, bytes - first);
                byte mask = ReadByte(source, sourceEndBit);
                if ((mask >> count) != 0)
                    throw new MissingPredictionBaselineException("Invalid authoritative input delta mask.");

                int bits = Math.Min(64, length - first * 8);
                ulong word = PeekWord(previousBuffer, previousOrigin + first * 8, bits);
                for (int i = 0; i < count; i++)
                {
                    if ((mask & (1 << i)) == 0)
                        continue;
                    int byteBits = Math.Min(8, length - (first + i) * 8);
                    byte value = ReadByte(source, sourceEndBit);
                    if ((value >> byteBits) != 0)
                        throw new MissingPredictionBaselineException("Invalid authoritative input delta padding.");
                    word = (word & ~(0xFFUL << (i * 8))) | ((ulong)value << (i * 8));
                }
                destination.WriteBits(word, (byte)bits);
            }
        }

        private static int PrefixBits(int length)
        {
            int bits = 8;
            for (int remaining = length >> 7; remaining != 0; remaining >>= 7)
                bits += 8;
            return bits;
        }

        private static byte ReadByte(BitPacker source, int endBit)
        {
            if (endBit - source.positionInBits < 8)
                throw new MissingPredictionBaselineException("Truncated authoritative input delta.");
            return (byte)source.ReadBits(8);
        }

        // Up to 64 bits starting at an arbitrary bit, with the bits past the requested count cleared.
        private static unsafe ulong PeekWord(byte[] buffer, int bitPosition, int bits)
        {
            int bytePosition = bitPosition >> 3;
            int shift = bitPosition & 7;
            ulong raw;
            if (BitConverter.IsLittleEndian && buffer.Length - bytePosition >= 9)
            {
                fixed (byte* b = &buffer[bytePosition])
                {
                    raw = *(ulong*)b >> shift;
                    if (shift + bits > 64)
                        raw |= (ulong)b[8] << (64 - shift);
                }
            }
            else
            {
                raw = 0;
                int needed = (shift + bits + 7) >> 3;
                for (int i = 0; i < needed && i < 8; i++)
                    raw |= (ulong)buffer[bytePosition + i] << (i * 8);
                raw >>= shift;
                if (needed > 8)
                    raw |= (ulong)buffer[bytePosition + 8] << (64 - shift);
            }
            return bits >= 64 ? raw : raw & ((1UL << bits) - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte PeekByte(in BitData data, int index, int length)
        {
            int position = (int)data.bitOrigin.value + index * 8;
            int bytePosition = position >> 3;
            int shift = position & 7;
            int bits = Math.Min(8, length - index * 8);
            var buffer = data.packer.buffer;
            int value = buffer[bytePosition] >> shift;
            if (shift + bits > 8)
                value |= buffer[bytePosition + 1] << (8 - shift);
            return (byte)(value & ((1 << bits) - 1));
        }
    }
}
