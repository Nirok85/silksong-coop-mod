using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class SessionStateTests
    {
        [Fact]
        public void SessionState_Transitions_HostLifecycle()
        {
            var session = new CoopSessionState();
            Assert.Equal(SessionStatus.Disconnected, session.Status);
            Assert.Equal(SessionRole.None, session.Role);

            var hostId = new NetworkPeerId(1, "HostPlayer");
            session.SetHosting(hostId, "SK-TEST-CODE");

            Assert.Equal(SessionStatus.Hosting, session.Status);
            Assert.Equal(SessionRole.Host, session.Role);
            Assert.True(session.IsHost);
            Assert.False(session.IsClient);
            Assert.False(session.IsConnected); // Waiting for peer

            var guestId = new NetworkPeerId(2, "GuestPlayer");
            session.SetConnected(guestId);

            Assert.Equal(SessionStatus.Connected, session.Status);
            Assert.True(session.IsConnected);
            Assert.Equal(guestId, session.RemotePeerId);

            session.SetDisconnected();
            Assert.Equal(SessionStatus.Disconnected, session.Status);
            Assert.Equal(SessionRole.None, session.Role);
            Assert.False(session.IsConnected);
        }

        [Fact]
        public void SessionState_Transitions_ClientLifecycle()
        {
            var session = new CoopSessionState();
            var clientId = new NetworkPeerId(2, "ClientPlayer");

            session.SetConnecting(clientId, "SK-TARGET-CODE");
            Assert.Equal(SessionStatus.Connecting, session.Status);
            Assert.Equal(SessionRole.Client, session.Role);
            Assert.True(session.IsClient);

            var hostId = new NetworkPeerId(1, "HostPlayer");
            session.SetConnected(hostId);
            Assert.Equal(SessionStatus.Connected, session.Status);
            Assert.True(session.IsConnected);

            session.SetSpectating();
            Assert.Equal(SessionStatus.Spectating, session.Status);
            Assert.True(session.IsConnected);

            session.ReturnToActive();
            Assert.Equal(SessionStatus.Connected, session.Status);

            session.SetTeamWipe();
            Assert.Equal(SessionStatus.TeamWipe, session.Status);
            Assert.True(session.IsConnected);
        }

        [Fact]
        public void SessionState_Metrics_TracksSentAndReceivedPackets()
        {
            var session = new CoopSessionState();

            session.RecordSent(100);
            session.RecordSent(50);
            session.RecordReceived(33);
            session.AdvanceTick();

            Assert.Equal((uint)2, session.SentPackets);
            Assert.Equal((ulong)150, session.SentBytes);
            Assert.Equal((uint)1, session.ReceivedPackets);
            Assert.Equal((ulong)33, session.ReceivedBytes);
            Assert.Equal((uint)1, session.CurrentTick);
        }
    }
}
