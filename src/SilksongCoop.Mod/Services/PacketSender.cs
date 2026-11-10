using System;
using System.Buffers;
using SilksongCoop.Core;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Shared serialization/send helper for wire packets. Centralizes renting the pooled
    /// buffer, writing the packet, broadcasting it and recording diagnostics so individual
    /// sync managers do not duplicate the same error-prone sequence.
    /// </summary>
    public static class PacketSender
    {
        /// <summary>
        /// Serializes <paramref name="packet"/> and sends it to every connected peer.
        /// </summary>
        /// <returns><c>true</c> when the packet was handed to the transport; <c>false</c>
        /// when the transport is missing or serialization/sending failed.</returns>
        public static bool SendToAll<T>(
            INetworkTransport transport,
            T packet,
            DeliveryMethod method,
            byte channel,
            int bufferSize,
            INetworkDiagnostics? diagnostics = null)
            where T : struct, IPacket
        {
            return Send(transport, null, packet, method, channel, bufferSize, diagnostics, null);
        }

        /// <summary>
        /// Same as <see cref="SendToAll{T}"/> but keeps a caller-specific log prefix so
        /// warnings still identify the manager that failed to send.
        /// </summary>
        public static bool SendToAll<T>(
            INetworkTransport transport,
            T packet,
            DeliveryMethod method,
            byte channel,
            int bufferSize,
            INetworkDiagnostics? diagnostics,
            string? logContext)
            where T : struct, IPacket
        {
            return Send(transport, null, packet, method, channel, bufferSize, diagnostics, logContext);
        }

        /// <summary>
        /// Serializes <paramref name="packet"/> and sends it to a single peer.
        /// </summary>
        public static bool SendTo<T>(
            INetworkTransport transport,
            NetworkPeerId target,
            T packet,
            DeliveryMethod method,
            byte channel,
            int bufferSize,
            INetworkDiagnostics? diagnostics = null)
            where T : struct, IPacket
        {
            return Send(transport, target, packet, method, channel, bufferSize, diagnostics, null);
        }

        private static bool Send<T>(
            INetworkTransport transport,
            NetworkPeerId? target,
            T packet,
            DeliveryMethod method,
            byte channel,
            int bufferSize,
            INetworkDiagnostics? diagnostics,
            string? logContext)
            where T : struct, IPacket
        {
            if (transport == null) return false;

            byte[]? buffer = null;
            try
            {
                buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);

                bool accepted = target.HasValue
                    ? transport.Send(target.Value, buffer, 0, writer.Position, method, channel)
                    : transport.SendToAll(buffer, 0, writer.Position, method, channel);
                if (!accepted) return false;

                diagnostics?.RecordSent(writer.Position);
                return true;
            }
            catch (Exception ex)
            {
                string prefix = string.IsNullOrEmpty(logContext) ? "[PacketSender]" : logContext!;
                CoopLog.Warn($"{prefix} Failed sending {typeof(T).Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
        }
    }
}
