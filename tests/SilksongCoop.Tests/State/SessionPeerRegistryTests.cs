using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class SessionPeerRegistryTests
    {
        [Fact]
        public void SetConnected_RegistersReadyPeer()
        {
            var session = new CoopSessionState();
            var peer = new NetworkPeerId(2, "Partner");

            session.SetConnected(peer, SessionRole.Client);

            Assert.Equal(1, session.Peers.Count);
            Assert.Single(session.Peers.ReadyPeers);
            Assert.True(session.Peers.TryGet(peer, out var record));
            Assert.Equal(PeerState.Ready, record.State);
        }

        [Fact]
        public void AddAndRemoveRemotePeer_KeepRegistryInSync()
        {
            var session = new CoopSessionState();
            var first = new NetworkPeerId(2);
            var second = new NetworkPeerId(3);

            session.AddRemotePeer(first);
            session.AddRemotePeer(second);

            Assert.Equal(2, session.Peers.Count);
            Assert.Equal(2, session.RemotePeerIds.Count);

            session.RemoveRemotePeer(first);

            Assert.Equal(1, session.Peers.Count);
            Assert.False(session.Peers.TryGet(first, out _));
            Assert.True(session.Peers.TryGet(second, out _));
        }

        [Fact]
        public void SetDisconnected_ClearsRegistry()
        {
            var session = new CoopSessionState();
            session.SetConnected(new NetworkPeerId(2), SessionRole.Host);
            Assert.Equal(1, session.Peers.Count);

            session.SetDisconnected();

            Assert.Equal(0, session.Peers.Count);
        }

        [Fact]
        public void StartingNewSession_ClearsPreviousPeers()
        {
            var session = new CoopSessionState();
            session.SetConnected(new NetworkPeerId(2), SessionRole.Host);
            Assert.Equal(1, session.Peers.Count);

            session.SetHosting(new NetworkPeerId(1), "room");

            Assert.Equal(0, session.Peers.Count);
        }
    }
}
