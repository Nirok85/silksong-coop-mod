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

        public PacketReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public int Position => _position;
        public int Length => _buffer.Length;
        public int Remaining => _buffer.Length - _position;

        public PacketHeader ReadHeader()
        {
            EnsureAvailable(PacketHeader.HeaderSize);
            var header = PacketHeader.ReadFrom(_buffer.Slice(_position, PacketHeader.HeaderSize));
            _position += PacketHeader.HeaderSize;
            return header;
        }

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

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _buffer[_position++];
        }

        public short ReadInt16()
        {
            EnsureAvailable(2);
            short val = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position, 2));
            _position += 2;
            return val;
        }

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            ushort val = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, 2));
            _position += 2;
            return val;
        }

        public int ReadInt32()
        {
            EnsureAvailable(4);
            int val = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return val;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return val;
        }

        public long ReadInt64()
        {
            EnsureAvailable(8);
            long val = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return val;
        }

        public ulong ReadUInt64()
        {
            EnsureAvailable(8);
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return val;
        }

        public float ReadFloat()
        {
            EnsureAvailable(4);
            int intVal = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
            _position += 4;
            return BitConverter.Int32BitsToSingle(intVal);
        }

        public double ReadDouble()
        {
            EnsureAvailable(8);
            long longVal = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
            _position += 8;
            return BitConverter.Int64BitsToDouble(longVal);
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            EnsureAvailable(count);
            var slice = _buffer.Slice(_position, count);
            _position += count;
            return slice;
        }

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
