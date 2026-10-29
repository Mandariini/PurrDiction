using System;
using System.Globalization;
using System.Text;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// Growable byte buffer with the little-endian and LEB128 writers the format uses.
    /// </summary>
    public sealed class RecordingBuffer
    {
        public byte[] data;
        public int length;

        public RecordingBuffer(int capacity = 256)
        {
            data = new byte[Math.Max(16, capacity)];
        }

        public void Clear() => length = 0;

        public void Ensure(int extra)
        {
            int needed = length + extra;
            if (needed <= data.Length)
                return;
            int size = data.Length;
            while (size < needed)
                size *= 2;
            Array.Resize(ref data, size);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            data[length++] = value;
        }

        public void WriteBytes(byte[] bytes, int offset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(bytes, offset, data, length, count);
            length += count;
        }

        public void WriteBytes(RecordingBuffer other) => WriteBytes(other.data, 0, other.length);

        public unsafe void WriteBytes(byte* source, int count)
        {
            if (count <= 0)
                return;
            Ensure(count);
            fixed (byte* dst = &data[length])
                Buffer.MemoryCopy(source, dst, count, count);
            length += count;
        }

        /// <summary>Replaces the contents with <paramref name="other"/>'s, keeping this buffer's array when it fits.</summary>
        public void CopyFrom(RecordingBuffer other)
        {
            length = 0;
            WriteBytes(other.data, 0, other.length);
        }

        /// <summary>Writes a string's UTF-8 bytes (no length prefix) without allocating.</summary>
        public void WriteUtf8(string value)
        {
            Ensure(Encoding.UTF8.GetMaxByteCount(value.Length));
            length += Encoding.UTF8.GetBytes(value, 0, value.Length, data, length);
        }

        public bool SameBytes(RecordingBuffer other) => SameBytes(0, length, other.data, 0, other.length);

        public void WriteVarUInt(ulong value)
        {
            Ensure(10);
            while (value >= 0x80)
            {
                data[length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            data[length++] = (byte)value;
        }

        public void WriteInt32(int value)
        {
            Ensure(4);
            data[length++] = (byte)value;
            data[length++] = (byte)(value >> 8);
            data[length++] = (byte)(value >> 16);
            data[length++] = (byte)(value >> 24);
        }

        public void WriteUInt64(ulong value)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++)
                data[length++] = (byte)(value >> (8 * i));
        }

        public void WriteVarInt(long value) => WriteVarUInt((ulong)((value << 1) ^ (value >> 63)));

        public unsafe void WriteFloat(float value)
        {
            Ensure(4);
            uint bits = *(uint*)&value;
            for (int i = 0; i < 4; i++)
                data[length++] = (byte)(bits >> (8 * i));
        }

        public unsafe void WriteDouble(double value)
        {
            Ensure(8);
            ulong bits = *(ulong*)&value;
            for (int i = 0; i < 8; i++)
                data[length++] = (byte)(bits >> (8 * i));
        }

        public void WriteString(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt((ulong)count);
            Ensure(count);
            Encoding.UTF8.GetBytes(value, 0, value.Length, data, length);
            length += count;
        }

        public byte[] ToArray()
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        public bool SameBytes(int offset, int count, byte[] other, int otherOffset, int otherCount)
        {
            if (count != otherCount)
                return false;
            for (int i = 0; i < count; i++)
                if (data[offset + i] != other[otherOffset + i])
                    return false;
            return true;
        }

        public bool SameBytes(int offset, int count, byte[] other)
        {
            if (other == null || other.Length != count)
                return false;
            for (int i = 0; i < count; i++)
                if (data[offset + i] != other[i])
                    return false;
            return true;
        }
    }
}
