using System;
using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;

namespace SilksongCoop.Benchmarks
{
    /// <summary>
    /// Core hot-path benchmarks. Run with:
    ///   dotnet run --project tools/SilksongCoop.Benchmarks -c Release -- --filter '*'
    /// A dry run (quick API smoke) is:
    ///   dotnet run --project tools/SilksongCoop.Benchmarks -c Release -- --job dry
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkDotNet.Running.BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args);
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class HeaderBenchmarks
    {
        private readonly PacketHeader _header = new(PacketType.TransformSync, 0, 42, 123456);
        private readonly byte[] _buffer = new byte[PacketHeader.HeaderSize];

        [Benchmark]
        public bool WriteAndRead()
        {
            _header.WriteTo(_buffer);
            return PacketHeader.TryReadFrom(_buffer, out _);
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class PacketBenchmarks
    {
        private readonly byte[] _buffer = new byte[128];
        private TransformSyncPacket _packet;

        [GlobalSetup]
        public void Setup()
        {
            _packet = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 7, 999),
                PositionX = 12.5f,
                PositionY = -3.25f,
                VelocityX = 4f,
                VelocityY = -1f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded,
                AnimationClipHash = 1234,
                NormalizedTime = 500,
                SceneNameHash = 77
            };
        }

        [Benchmark]
        public int SerializeDeserialize()
        {
            var writer = new PacketWriter(_buffer);
            _packet.Serialize(ref writer);
            var reader = new PacketReader(_buffer.AsSpan(0, writer.Position));
            var decoded = TransformSyncPacket.Deserialize(ref reader);
            return decoded.AnimationClipHash;
        }

        [Benchmark]
        public bool EnemySnapshotBatchSerialize()
        {
            var writer = new PacketWriter(_buffer);
            var packet = new EnemySnapshotPacket
            {
                Header = new PacketHeader(PacketType.EnemySnapshot, NetworkChannels.World, 1, 1000)
            };
            packet.Serialize(ref writer);
            return writer.Position > 0;
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class StateBenchmarks
    {
        private EnemySyncService _service = null!;
        private ulong _key;
        private int _hp;
        private uint _sequence;

        [GlobalSetup]
        public void Setup()
        {
            _service = new EnemySyncService();
            _key = 0xABCDEF;
            _hp = 10_000_000;
            _sequence = 0;
            _service.TryAcceptHit(_key, _hp, dead: false);
        }

        [Benchmark]
        public bool EnemyHitMerge()
        {
            _hp--;
            _sequence++;
            return _service.TryAcceptRemoteHit(
                _key, _hp, dead: false, damage: 1, senderId: 2, sequence: _sequence,
                RemoteHitPolicy.LocalHost, out _);
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class BufferBenchmarks
    {
        private SnapshotBuffer _buffer = null!;
        private TransformSnapshot _snapshot;
        private int _tick;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new SnapshotBuffer(64);
            _snapshot = new TransformSnapshot();
            for (int i = 0; i < 64; i++)
            {
                _snapshot.TimestampMs = i * 33.0;
                _snapshot.Tick = (uint)i;
                _buffer.AddSnapshot(_snapshot);
            }
        }

        [Benchmark]
        public bool AddAndInterpolate()
        {
            _snapshot.TimestampMs += 33.0;
            _snapshot.Tick = (uint)(++_tick + 64);
            _buffer.AddSnapshot(_snapshot);
            return _buffer.TryGetSnapshotsForTime(_snapshot.TimestampMs - 16.0, out _, out _, out _);
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class NetworkingBenchmarks
    {
        private RateLimiter _limiter = null!;
        private NetworkPeerId _peer;
        private string _roomCode = null!;

        [GlobalSetup]
        public void Setup()
        {
            _limiter = new RateLimiter(maxTokens: 1_000_000, refillRatePerSecond: 100_000);
            _peer = new NetworkPeerId(42);
            _roomCode = RoomCodeCodec.Encode(IPAddress.Loopback, 7777);
        }

        [Benchmark]
        public bool RateLimitConsume() => _limiter.TryConsume(_peer);

        [Benchmark]
        public bool RoomCodeDecode() =>
            RoomCodeCodec.TryDecode(_roomCode, out _, out _);
    }
}
