using System;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.EndToEnd
{
    /// <summary>
    /// The reliable backlog cap is a hard backpressure limit: packets above it are dropped.
    /// Before this counter existed the drop was only a log line and callers could not tell
    /// that a state update never left the machine. This test fills the backlog over a real
    /// loopback socket and verifies the diagnostic is observable.
    /// </summary>
    public class ReliableBacklogDropTests
    {
        [Fact]
        public void FilledBacklog_IsObservableThroughTheDiagnosticsCounter()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Stop polling the host: it can no longer ACK, so the client's unacknowledged
            // queue grows until the cap is hit.
            byte[] payload = BitConverter.GetBytes(123456);
            for (int i = 0; i < 700; i++)
            {
                client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);
            }

            Assert.True(client.ReliableBacklogCount >= 512);
            Assert.True(client.DroppedReliablePackets > 0);
            Assert.True(client is IReliableDropDiagnostics);
        }

        private static void PumpUntil(Func<bool> condition, LiteNetLibTransport host, LiteNetLibTransport client, int maxMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                try
                {
                    host.PollEvents();
                    client.PollEvents();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                Thread.Sleep(10);
            }
        }
    }
}
