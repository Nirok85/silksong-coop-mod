using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// High-performance UDP socket network transport supporting unreliable, sequenced,
    /// and reliable ordered delivery channels with room-code and direct IP:port connection.
    /// </summary>
    public class LiteNetLibTransport : INetworkTransport
    {
        private enum UdpFrameType : byte
        {
            Handshake = 0x01,
            HandshakeAck = 0x02,
            Data = 0x03,
            Ack = 0x04,
            Ping = 0x05,
            Pong = 0x06,
            Disconnect = 0x07
        }

        private struct SentReliablePacket
        {
            public uint Sequence;
            public byte Channel;
            public byte[] Envelope;
            public long LastSentTimeMs;
            public int Retries;
        }

        private Socket? _socket;
        private EndPoint? _remoteEndPoint;
        private NetworkPeerId? _remotePeerId;
        private readonly object _lock = new();

        private readonly Dictionary<byte, uint> _sendSequences = new();
        private readonly Dictionary<byte, uint> _expectedReceiveSequences = new();
        private readonly Dictionary<byte, uint> _latestSequencedReceived = new();
        private readonly Dictionary<byte, SortedDictionary<uint, byte[]>> _pendingReliable = new();
        private readonly LinkedList<SentReliablePacket> _unacknowledged = new();
        private const int MaxUnacknowledgedPackets = 512;

        private long _lastPingSentMs;
        private int _rttMs = 30;
        // Connection attempt tracking for client-side timeout (UX: avoid stuck "Connecting" state)
        private long _connectionAttemptStartMs;
        private const long ConnectionTimeoutMs = 10000;

        // Pre-allocated receive buffer to avoid per-poll GC pressure
        private readonly byte[] _recvBuffer = new byte[2048];

        private static long GetTimestampMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }
        public NetworkPeerId? RemotePeerId => _remotePeerId;
        public int RoundTripTimeMs => _rttMs;
        public int BoundPort { get; private set; }

        public LiteNetLibTransport(ulong peerId = 1, string displayName = "Player")
        {
            LocalPeerId = new NetworkPeerId(peerId, displayName);
        }

        private void ResetSessionState()
        {
            _sendSequences.Clear();
            _expectedReceiveSequences.Clear();
            _latestSequencedReceived.Clear();
            _pendingReliable.Clear();
            _unacknowledged.Clear();
            _lastPingSentMs = 0;
            _rttMs = 30;
            _connectionAttemptStartMs = 0;
        }

        public void StartHost(int port = 7777)
        {
            lock (_lock)
            {
                CloseSocket();
                ResetSessionState();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };

                // Enable socket reuse and avoid ICMP unreachable errors on Windows
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                const int SIO_UDP_CONNRESET = -1744830452;
                try
                {
                    _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - this is Windows-specific optimization
                    System.Diagnostics.Debug.WriteLine($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
                BoundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
                _remoteEndPoint = null;
                _remotePeerId = null;
                IsHost = true;
                IsConnected = false;
            }
        }

        public void Connect(string addressOrCode)
        {
            IPAddress targetIp;
            int targetPort;

            if (RoomCodeCodec.TryDecode(addressOrCode, out IPAddress decodedIp, out int decodedPort))
            {
                targetIp = decodedIp;
                targetPort = decodedPort;
            }
            else
            {
                // Parse standard host:port or IP:port
                string[] parts = addressOrCode.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out int p))
                {
                    targetPort = p;
                    if (!IPAddress.TryParse(parts[0], out targetIp!))
                    {
                        try
                        {
                            var addresses = Dns.GetHostAddresses(parts[0]);
                            if (addresses.Length == 0)
                                throw new ArgumentException($"DNS resolution returned no addresses for: {parts[0]}");
                            targetIp = addresses[0];
                        }
                        catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
                        {
                            throw new ArgumentException($"Failed to resolve hostname '{parts[0]}': {ex.Message}", nameof(addressOrCode), ex);
                        }
                    }
                }
                else if (IPAddress.TryParse(addressOrCode, out targetIp!))
                {
                    targetPort = 7777;
                }
                else
                {
                    throw new ArgumentException($"Invalid address or room code: {addressOrCode}", nameof(addressOrCode));
                }
            }

            lock (_lock)
            {
                CloseSocket();
                ResetSessionState();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };

                // Enable socket reuse and avoid ICMP unreachable errors on Windows
                const int SIO_UDP_CONNRESET = -1744830452;
                try
                {
                    _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - this is Windows-specific optimization
                    System.Diagnostics.Debug.WriteLine($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                BoundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

                _remoteEndPoint = new IPEndPoint(targetIp, targetPort);
                _remotePeerId = null;
                IsHost = false;
                IsConnected = false;
                _connectionAttemptStartMs = GetTimestampMs();

                // Send initial handshake
                SendEnvelope(UdpFrameType.Handshake, channel: 0, sequence: 0, method: DeliveryMethod.Unreliable, payload: BitConverter.GetBytes(LocalPeerId.Value));
            }
        }

        public void Disconnect() => Disconnect(DisconnectReason.GracefulDisconnect);

        public void Disconnect(DisconnectReason reason)
        {
            lock (_lock)
            {
                // Fix: Use || instead of && to properly check for disconnected state
                if (_socket == null || !IsConnected)
                {
                    return;
                }

                if (_remoteEndPoint != null)
                {
                    SendEnvelope(UdpFrameType.Disconnect, 0, 0, DeliveryMethod.Unreliable, ReadOnlySpan<byte>.Empty);
                }

                bool wasConnected = IsConnected;
                var peer = _remotePeerId ?? new NetworkPeerId(0);
                IsConnected = false;
                _remoteEndPoint = null;
                _remotePeerId = null;

                ResetSessionState();
                CloseSocket();

                if (wasConnected)
                {
                    OnPeerDisconnected?.Invoke(peer, reason);
                }
            }
        }

        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            lock (_lock)
            {
                if (!IsConnected || _remoteEndPoint == null || _socket == null) return;

                uint seq;
                if (!_sendSequences.TryGetValue(channel, out seq))
                    seq = 0;
                _sendSequences[channel] = seq + 1;

                byte[] payload = new byte[length];
                Buffer.BlockCopy(data, offset, payload, 0, length);

                byte[] envelope = BuildEnvelope(UdpFrameType.Data, channel, seq, method, payload);
                try
                {
                    _socket.SendTo(envelope, _remoteEndPoint);
                }
                catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
                catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }

                if (method == DeliveryMethod.ReliableOrdered)
                {
                    // Fix: Cap unacknowledged packets to prevent unbounded memory growth
                    if (_unacknowledged.Count >= MaxUnacknowledgedPackets)
                    {
                        // Drop oldest packet to make room (O(1) with LinkedList)
                        _unacknowledged.RemoveFirst();
                    }
                    _unacknowledged.AddLast(new SentReliablePacket
                    {
                        Sequence = seq,
                        Channel = channel,
                        Envelope = envelope,
                        LastSentTimeMs = GetTimestampMs(),
                        Retries = 0
                    });
                }
            }
        }

        public void PollEvents()
        {
            lock (_lock)
            {
                if (_socket == null) return;

                long now = GetTimestampMs();

                // 1. Receive incoming datagrams - reuse pre-allocated buffer
                EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

                while (true)
                {
                    Socket? socket = _socket;
                    if (socket == null) break;

                    int available;
                    try
                    {
                        available = socket.Available;
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (available <= 0) break;

                    try
                    {
                        int received = socket.ReceiveFrom(_recvBuffer, ref senderEp);
                        if (received >= 8) // Minimum envelope header
                        {
                            ProcessIncomingDatagram(_recvBuffer.AsSpan(0, received), senderEp);
                        }
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }

                // Client-side connection timeout: avoid stuck "Connecting" state (UX)
                if (!IsHost && !IsConnected && _remoteEndPoint != null && _socket != null && _connectionAttemptStartMs > 0)
                {
                    if (GetTimestampMs() - _connectionAttemptStartMs > ConnectionTimeoutMs)
                    {
                        // Manual cleanup since Disconnect() guard skips non-connected state
                        var peer = _remotePeerId ?? new NetworkPeerId(0);
                        IsConnected = false;
                        var ep = _remoteEndPoint;
                        _remoteEndPoint = null;
                        _remotePeerId = null;
                        _connectionAttemptStartMs = 0;
                        ResetSessionState();
                        CloseSocket();
                        OnPeerDisconnected?.Invoke(peer, DisconnectReason.Timeout);
                        return;
                    }
                }

                // Early exit if disconnected or socket closed during datagram processing
                if (_socket == null || !IsConnected || _remoteEndPoint == null)
                {
                    return;
                }

                // 2. Retransmit unacknowledged reliable packets
                var node = _unacknowledged.First;
                while (node != null)
                {
                    var next = node.Next;
                    var item = node.Value;
                    if (now - item.LastSentTimeMs > 100) // 100ms retransmit timeout
                    {
                        if (item.Retries >= 10) // Fix: Use >= to allow exactly 10 retries (0-9)
                        {
                            // Connection lost
                            Disconnect(DisconnectReason.Timeout);
                            return;
                        }

                        item.Retries++;
                        item.LastSentTimeMs = now;
                        node.Value = item;

                        if (_remoteEndPoint != null && _socket != null)
                        {
                            try
                            {
                                _socket.SendTo(item.Envelope, _remoteEndPoint);
                            }
                            catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
                            catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
                        }
                    }
                    node = next;
                }

                // 3. Heartbeat ping
                if (IsConnected && _remoteEndPoint != null && _socket != null)
                {
                    if (now - _lastPingSentMs > 1000)
                    {
                        _lastPingSentMs = now;
                        byte[] timestampPayload = BitConverter.GetBytes(now);
                        SendEnvelope(UdpFrameType.Ping, 0, 0, DeliveryMethod.Unreliable, timestampPayload);
                    }
                }
            }
        }

        private void ProcessIncomingDatagram(ReadOnlySpan<byte> datagram, EndPoint sender)
        {
            UdpFrameType frameType = (UdpFrameType)datagram[0];
            byte channel = datagram[1];
            DeliveryMethod method = (DeliveryMethod)datagram[2];
            uint sequence = BitConverter.ToUInt32(datagram.Slice(3, 4));
            byte payloadLenByte = datagram[7]; // or length calculation
            ReadOnlySpan<byte> payload = datagram.Slice(8);

            switch (frameType)
            {
                case UdpFrameType.Handshake:
                    if (IsHost && !IsConnected && _remoteEndPoint == null)
                    {
                        // Validate sender is not ourselves
                        if (sender is IPEndPoint ep && ep.Address.Equals(IPAddress.Loopback) && ep.Port == BoundPort)
                            break; // Ignore self-handshake

                        _remoteEndPoint = sender;
                        ulong clientPeerValue = payload.Length >= 8 ? BitConverter.ToUInt64(payload) : 2;
                        _remotePeerId = new NetworkPeerId(clientPeerValue, "Client");
                        IsConnected = true;

                        // Send HandshakeAck back
                        SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BitConverter.GetBytes(LocalPeerId.Value));
                        OnPeerConnected?.Invoke(_remotePeerId.Value);
                    }
                    break;

                case UdpFrameType.HandshakeAck:
                    if (!IsHost && !IsConnected)
                    {
                        _remoteEndPoint = sender;
                        ulong hostPeerValue = payload.Length >= 8 ? BitConverter.ToUInt64(payload) : 1;
                        _remotePeerId = new NetworkPeerId(hostPeerValue, "Host");
                        IsConnected = true;

                        OnPeerConnected?.Invoke(_remotePeerId.Value);
                    }
                    break;

                case UdpFrameType.Data:
                    // If reliable, send ACK immediately
                    if (method == DeliveryMethod.ReliableOrdered)
                    {
                        byte[] ackPayload = new byte[5];
                        ackPayload[0] = channel;
                        Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, ackPayload, 1, 4);
                        SendEnvelope(UdpFrameType.Ack, channel, sequence, DeliveryMethod.Unreliable, ackPayload);
                    }

                    HandleIncomingData(channel, method, sequence, payload);
                    break;

                case UdpFrameType.Ack:
                    if (payload.Length >= 5)
                    {
                        byte ackChannel = payload[0];
                        uint ackSeq = BitConverter.ToUInt32(payload.Slice(1, 4));

                        var ackNode = _unacknowledged.First;
                        while (ackNode != null)
                        {
                            var ackNext = ackNode.Next;
                            if (ackNode.Value.Channel == ackChannel && ackNode.Value.Sequence == ackSeq)
                            {
                                _unacknowledged.Remove(ackNode);
                            }
                            ackNode = ackNext;
                        }
                    }
                    break;

                case UdpFrameType.Ping:
                    SendEnvelope(UdpFrameType.Pong, 0, 0, DeliveryMethod.Unreliable, payload);
                    break;

                case UdpFrameType.Pong:
                    if (payload.Length >= 8)
                    {
                        long sentTime = BitConverter.ToInt64(payload);
                        long rtt = GetTimestampMs() - sentTime;
                        if (rtt >= 0 && rtt < 2000)
                        {
                            _rttMs = (int)rtt;
                        }
                    }
                    break;

                case UdpFrameType.Disconnect:
                    if (IsConnected)
                    {
                        var peer = _remotePeerId ?? new NetworkPeerId(0);
                        IsConnected = false;
                        _remoteEndPoint = null;
                        _remotePeerId = null;
                        ResetSessionState();
                        OnPeerDisconnected?.Invoke(peer, DisconnectReason.GracefulDisconnect);
                    }
                    break;
            }
        }

        private void HandleIncomingData(byte channel, DeliveryMethod method, uint sequence, ReadOnlySpan<byte> payload)
        {
            byte[] data = payload.ToArray();

            if (method == DeliveryMethod.ReliableOrdered)
            {
                if (!_pendingReliable.TryGetValue(channel, out var pending))
                {
                    pending = new SortedDictionary<uint, byte[]>();
                    _pendingReliable[channel] = pending;
                }

                _expectedReceiveSequences.TryGetValue(channel, out uint expected);

                if (sequence == expected)
                {
                    DispatchData(data);
                    if (!IsConnected) return;

                    expected++;
                    _expectedReceiveSequences[channel] = expected;

                    while (pending.TryGetValue(expected, out var nextData))
                    {
                        pending.Remove(expected);
                        DispatchData(nextData);
                        if (!IsConnected) return;

                        expected++;
                        _expectedReceiveSequences[channel] = expected;
                    }
                }
                else if (sequence > expected)
                {
                    pending[sequence] = data;
                }
            }
            else if (method == DeliveryMethod.Sequenced)
            {
                _latestSequencedReceived.TryGetValue(channel, out uint latest);
                if (sequence >= latest)
                {
                    _latestSequencedReceived[channel] = sequence + 1;
                    DispatchData(data);
                }
            }
            else
            {
                DispatchData(data);
            }
        }

        private void DispatchData(byte[] data)
        {
            // Fix: Ensure we have a valid peer before dispatching
            if (!_remotePeerId.HasValue)
            {
                // No remote peer established yet, drop the data
                return;
            }
            var peer = _remotePeerId.Value;
            OnDataReceived?.Invoke(peer, data, 0, data.Length);
        }

        private void SendEnvelope(UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            if (_socket == null || _remoteEndPoint == null) return;

            byte[] envelope = BuildEnvelope(type, channel, sequence, method, payload);
            try
            {
                _socket.SendTo(envelope, _remoteEndPoint);
            }
            catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
            catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
        }

        private byte[] BuildEnvelope(UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            byte[] buffer = new byte[8 + payload.Length];
            buffer[0] = (byte)type;
            buffer[1] = channel;
            buffer[2] = (byte)method;
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, buffer, 3, 4);
            buffer[7] = 0; // Flags/Reserved
            payload.CopyTo(buffer.AsSpan(8));
            return buffer;
        }

        private void CloseSocket()
        {
            var socket = _socket;
            _socket = null; // Clear reference first to prevent use-after-close
            if (socket != null)
            {
                try
                {
                    socket.Close();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Socket close error: {ex.Message}"); }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
