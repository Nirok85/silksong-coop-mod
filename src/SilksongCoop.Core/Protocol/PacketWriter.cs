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
        #region Fields and Constructor

        private readonly Span<byte> _buffer;
        private int _position;

        /// <summary>
        /// Initializes a new packet writer with the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to write packet data into.</param>
        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        #endregion

        #region Properties

        public int Position => _position;
        public int Capacity => _buffer.Length;
        public int Remaining => _buffer.Length - _position;
        
        /// <summary>
        /// Gets a read-only span over the bytes that have been written so far.
        /// </summary>
        public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _position);

        #endregion

        #region Header and Primitive Writers

        /// <summary>
        /// Writes a packet header to the buffer.
        /// </summary>
        /// <param name="header">The packet header to write.</param>
        public void WriteHeader(PacketHeader header)
        {
            header.WriteTo(_buffer.Slice(_position));
            _position += PacketHeader.HeaderSize;
        }

        /// <summary>
        /// Writes a single byte to the buffer.
        /// </summary>
        /// <param name="value">The byte value to write.</param>
        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        /// <summary>
        /// Writes a 16-bit signed integer to the buffer.
        /// </summary>
        /// <param name="value">The Int16 value to write.</param>
        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position, 2), value);
            _position += 2;
        }

        /// <summary>
        /// Writes a 16-bit unsigned integer to the buffer.
        /// </summary>
        /// <param name="value">The UInt16 value to write.</param>
        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position, 2), value);
            _position += 2;
        }

        /// <summary>
        /// Writes a 32-bit signed integer to the buffer.
        /// </summary>
        /// <param name="value">The Int32 value to write.</param>
        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position, 4), value);
            _position += 4;
        }

        /// <summary>
        /// Writes a 32-bit unsigned integer to the buffer.
        /// </summary>
        /// <param name="value">The UInt32 value to write.</param>
        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position, 4), value);
            _position += 4;
        }

        /// <summary>
        /// Writes a 64-bit signed integer to the buffer.
        /// </summary>
        /// <param name="value">The Int64 value to write.</param>
        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position, 8), value);
            _position += 8;
        }

        /// <summary>
        /// Writes a raw byte span to the buffer.
        /// </summary>
        public void WriteRaw(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.Slice(_position));
            _position += data.Length;
        }

        /// <summary>
        /// Writes a 64-bit unsigned integer to the buffer.
        /// </summary>
        /// <param name="value">The UInt64 value to write.</param>
        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position, 8), value);
            _position += 8;
        }

        /// <summary>
        /// Writes a 32-bit floating-point value to the buffer.
        /// </summary>
        /// <param name="value">The float value to write.</param>
        public void WriteFloat(float value)
        {
            EnsureCapacity(4);
            int intVal = BitConverter.SingleToInt32Bits(value);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position, 4), intVal);
            _position += 4;
        }

        /// <summary>
        /// Writes a 64-bit floating-point value to the buffer.
        /// </summary>
        /// <param name="value">The double value to write.</param>
        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            long longVal = BitConverter.DoubleToInt64Bits(value);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position, 8), longVal);
            _position += 8;
        }

        /// <summary>
        /// Writes a boolean value to the buffer as a single byte.
        /// </summary>
        /// <param name="value">The boolean value to write.</param>
        public void WriteBool(bool value)
        {
            WriteByte(value ? (byte)1 : (byte)0);
        }

        #endregion

        #region Complex Type Writers

        /// <summary>
        /// Writes a sequence of bytes to the buffer.
        /// </summary>
        /// <param name="bytes">The bytes to write.</param>
        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(bytes.Length);
            bytes.CopyTo(_buffer.Slice(_position));
            _position += bytes.Length;
        }

        /// <summary>
        /// Writes a UTF-8 encoded string to the buffer with a length prefix.
        /// </summary>
        /// <param name="value">The string to write.</param>
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

        /// <summary>
        /// Writes a 128-bit GUID to the buffer.
        /// </summary>
        /// <param name="guid">The GUID value to write.</param>
        public void WriteGuid(Guid guid)
        {
            EnsureCapacity(16);
            if (!guid.TryWriteBytes(_buffer.Slice(_position, 16)))
                throw new InvalidOperationException("Failed to write GUID bytes");
            _position += 16;
        }

        #endregion

        private void EnsureCapacity(int size)
        {
            if (_position + size > _buffer.Length)
                throw new InvalidOperationException($"Buffer overflow: required {size} bytes, but only {_buffer.Length - _position} bytes remain.");
        }
    }
}
