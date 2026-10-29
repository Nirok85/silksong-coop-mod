using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using UnityEngine;

namespace SilksongCoop.Mod.Networking
{
    /// <summary>
    /// Aggregates small packets that share the same delivery method and channel into a
    /// single datagram (<see cref="PacketType.Batch"/>) to cut per-packet overhead.
    /// Entries are copied on enqueue, so callers may return their pooled buffers
    /// immediately. A group with a single entry is sent raw to avoid the envelope cost.
    /// </summary>
    public class PacketBatcher
    {
        private readonly INetworkTransport _transport;
        private readonly NetworkPeerId _peerId;
        private readonly int _maxBatchBytes;
        private readonly int _maxBatchEntries;
        private readonly float _maxBatchDelaySeconds;
        private readonly uint _timestamp;

        private byte[] _buffer;
        private int _offset;
        private int _count;
        private DeliveryMethod _method;
        private byte _channel;
        private bool _hasGroup;
        private float _groupStartTime;

        public PacketBatcher(
            INetworkTransport transport,
            NetworkPeerId peerId,
            int maxBatchSize = 16,
            float maxBatchDelayMs = 8f,
            int maxBatchBytes = 1200)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _peerId = peerId;
            _maxBatchEntries = Math.Max(2, Math.Min(PacketBatch.MaxEntries, maxBatchSize));
            _maxBatchDelaySeconds = Math.Max(0f, maxBatchDelayMs) / 1000f;
            _maxBatchBytes = Math.Max(PacketBatch.HeaderSize + 64, maxBatchBytes);
            _buffer = new byte[_maxBatchBytes];
            _timestamp = (uint)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
        }

        public int PendingCount => _count;

        /// <summary>
        /// Queues a serialized packet for the next flush. The payload is copied immediately.
        /// </summary>
        public void Enqueue(byte[] data, int length, byte channel, DeliveryMethod method)
        {
            if (data == null || length <= 0) return;

            if (_hasGroup && (method != _method || channel != _channel))
                Flush();

            if (!_hasGroup)
                BeginGroup(method, channel);

            if (!PacketBatch.TryWriteEntry(_buffer, _offset, new ReadOnlySpan<byte>(data, 0, length), out int written))
            {
                // The current group is full: flush it and retry in a fresh one.
                Flush();
                BeginGroup(method, channel);

                if (!PacketBatch.TryWriteEntry(_buffer, _offset, new ReadOnlySpan<byte>(data, 0, length), out written))
                {
                    // A single packet larger than the batch buffer: send it unchanged.
                    _hasGroup = false;
                    _count = 0;
                    _offset = 0;
                    TrySendRaw(data, 0, length, method, channel);
                    return;
                }
            }

            _offset += written;
            _count++;

            if (_count >= _maxBatchEntries)
                Flush();
        }

        /// <summary>
        /// Flushes the pending group once the maximum batching delay has elapsed.
        /// Called every frame so latency stays bounded.
        /// </summary>
        public void Update()
        {
            if (!_hasGroup || _count == 0) return;
            if (Time.unscaledTime - _groupStartTime >= _maxBatchDelaySeconds)
                Flush();
        }

        /// <summary>
        /// Sends the pending group immediately.
        /// </summary>
        public void Flush()
        {
            if (!_hasGroup) return;

            int count = _count;
            int offset = _offset;
            DeliveryMethod method = _method;
            byte channel = _channel;

            _hasGroup = false;
            _count = 0;
            _offset = 0;

            if (count == 0) return;

            if (count == 1)
            {
                int payloadOffset = PacketBatch.HeaderSize + PacketBatch.EntryLengthSize;
                int payloadLength = offset - payloadOffset;
                if (payloadLength > 0)
                    TrySendRaw(_buffer, payloadOffset, payloadLength, method, channel);
                return;
            }

            try
            {
                PacketBatch.WriteHeader(_buffer, channel, _timestamp, (byte)count);
                _transport.Send(_peerId, _buffer, 0, offset, method, channel);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[PacketBatcher] Batch send failed: {ex.Message}");
            }
        }

        private void BeginGroup(DeliveryMethod method, byte channel)
        {
            _hasGroup = true;
            _method = method;
            _channel = channel;
            _groupStartTime = Time.unscaledTime;
            _offset = PacketBatch.HeaderSize;
            _count = 0;
        }

        private void TrySendRaw(byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            try
            {
                _transport.Send(_peerId, data, offset, length, method, channel);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[PacketBatcher] Send failed: {ex.Message}");
            }
        }
    }
}
