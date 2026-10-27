using System;
using System.Buffers.Binary;
using System.Text;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Zero-allocation ref struct for deserializing binary protocol data from a ReadOnlySpan.
    /// </summary>
    public ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _position;

        /// <summary>
        /// Initializes a packet reader over the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read from.</param>
        public PacketReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public int Position => _position;
        public int Length => _buffer.Length;
        public int Remaining => _buffer.Length - _position;

        /// <summary>
        /// Reads and advances past a packet header.
        /// </summary>
        /// <returns>The parsed packet header.</returns>
        public PacketHeader ReadHeader()
        {
            EnsureAvailable(PacketHeader.HeaderSize);
            var header = PacketHeader.ReadFrom(_buffer.Slice(_position, PacketHeader.HeaderSize));
            _position += PacketHeader.HeaderSize;
            return header;
        }

        /// <summary>
        /// Attempts to read a packet header if enough bytes remain.
        /// </summary>
        /// <param name="header">Output header if successful.</param>
        /// <returns>True if a valid header was read; otherwise, false.</returns>
        public bool TryReadHeader(out PacketHeader header)
        {
            header = default;
            if (Remaining < PacketHeader.HeaderSize)
                return false;

            if (PacketHeader.TryReadFrom(_buffer.Slice(_position, PacketHeader.HeaderSize), out header))
            {
                _position += PacketHeader.HeaderSize;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads a single byte from the buffer.
        /// </summary>
        /// <returns>The byte value.</returns>
        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _buffer[_position++];
        }

        /// <summary>
        /// Reads a 16-bit signed integer from the buffer.
        /// </summary>
        /// <returns>The Int16 value.</returns>
        public short ReadInt16()
        {
            EnsureAvailable(2);
            short val = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position, 2));
            _position += 2;
            return val;
        }

        /// <summary>
        /// Reads a 16-bit unsigned integer from the buffer.
        /// </summary>
        /// <returns>The UInt16 value.</returns>
        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            ushort val = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, 2));
            _position += 2;
            return val;
        }

        /// <summary>
        /// Reads a 32-bit signed integer from the buffer.
        /// </summary>
        /// <returns>The Int32 value.</returns>
        public int ReadInt32()
        {
            EnsureAvailable(4);
            int val = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return val;
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer from the buffer.
        /// </summary>
        /// <returns>The UInt32 value.</returns>
        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return val;
        }

        /// <summary>
        /// Reads a 64-bit signed integer from the buffer.
        /// </summary>
        /// <returns>The Int64 value.</returns>
        public long ReadInt64()
        {
            EnsureAvailable(8);
            long val = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return val;
        }

        /// <summary>
        /// Reads a 64-bit unsigned integer from the buffer.
        /// </summary>
        /// <returns>The UInt64 value.</returns>
        public ulong ReadUInt64()
        {
            EnsureAvailable(8);
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return val;
        }

        /// <summary>
        /// Reads a 32-bit floating point value from the buffer.
        /// </summary>
        /// <returns>The float value.</returns>
        public float ReadFloat()
        {
            EnsureAvailable(4);
            int intVal = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return BitConverter.Int32BitsToSingle(intVal);
        }

        /// <summary>
        /// Reads a 64-bit floating point value from the buffer.
        /// </summary>
        /// <returns>The double value.</returns>
        public double ReadDouble()
        {
            EnsureAvailable(8);
            long longVal = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return BitConverter.Int64BitsToDouble(longVal);
        }

        /// <summary>
        /// Reads a boolean value from the buffer.
        /// </summary>
        /// <returns>True if the byte is non-zero; otherwise, false.</returns>
        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        /// <summary>
        /// Reads a specified number of bytes from the buffer.
        /// </summary>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>A span containing the bytes read.</returns>
        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            EnsureAvailable(count);
            var slice = _buffer.Slice(_position, count);
            _position += count;
            return slice;
        }

        /// <summary>
        /// Reads a UTF-8 string from the buffer.
        /// </summary>
        /// <returns>The string value, or null if the length prefix is negative.</returns>
        public string? ReadString()
        {
            short byteCount = ReadInt16();
            if (byteCount < 0) return null;
            if (byteCount == 0) return string.Empty;

            EnsureAvailable(byteCount);
            string str = Encoding.UTF8.GetString(_buffer.Slice(_position, byteCount));
            _position += byteCount;
            return str;
        }

        /// <summary>
        /// Reads a 128-bit GUID from the buffer.
        /// </summary>
        /// <returns>The GUID value.</returns>
        public Guid ReadGuid()
        {
            EnsureAvailable(16);
            var guid = new Guid(_buffer.Slice(_position, 16));
            _position += 16;
            return guid;
        }

        private void EnsureAvailable(int count)
        {
            if (_position + count > _buffer.Length)
                throw new InvalidOperationException($"Buffer underflow: requested {count} bytes, but only {_buffer.Length - _position} bytes remain.");
        }
    }
}
