using System;
using System.Buffers.Binary;
using System.Text;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Zero-allocation ref struct for serializing binary protocol data into a target Span.
    /// </summary>
    public ref struct PacketWriter
    {
        private readonly Span<byte> _buffer;
        private int _position;

        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public int Position => _position;
        public int Capacity => _buffer.Length;
        public int Remaining => _buffer.Length - _position;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _position);

        public void WriteHeader(PacketHeader header)
        {
            header.WriteTo(_buffer.Slice(_position));
            _position += PacketHeader.HeaderSize;
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position, 2), value);
            _position += 2;
        }

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position, 2), value);
            _position += 2;
        }

        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position, 4), value);
            _position += 4;
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position, 4), value);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position, 8), value);
            _position += 8;
        }

        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position, 8), value);
            _position += 8;
        }

        public void WriteFloat(float value)
        {
            EnsureCapacity(4);
            int intVal = BitConverter.SingleToInt32Bits(value);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position, 4), intVal);
            _position += 4;
        }

        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            long longVal = BitConverter.DoubleToInt64Bits(value);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position, 8), longVal);
            _position += 8;
        }

        public void WriteBool(bool value)
        {
            WriteByte(value ? (byte)1 : (byte)0);
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(bytes.Length);
            bytes.CopyTo(_buffer.Slice(_position));
            _position += bytes.Length;
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt16(-1);
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > short.MaxValue)
                throw new ArgumentException("String exceeds maximum length of 32767 bytes", nameof(value));

            WriteInt16((short)byteCount);
            EnsureCapacity(byteCount);
            Encoding.UTF8.GetBytes(value, _buffer.Slice(_position, byteCount));
            _position += byteCount;
        }

        public void WriteGuid(Guid guid)
        {
            EnsureCapacity(16);
            if (!guid.TryWriteBytes(_buffer.Slice(_position, 16)))
                throw new InvalidOperationException("Failed to write GUID bytes");
            _position += 16;
        }

        private void EnsureCapacity(int size)
        {
            if (_position + size > _buffer.Length)
                throw new InvalidOperationException($"Buffer overflow: required {size} bytes, but only {_buffer.Length - _position} bytes remain.");
        }
    }
}
