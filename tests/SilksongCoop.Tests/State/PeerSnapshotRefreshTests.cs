using System.Reflection;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Cached peer snapshots must refresh when the numeric peer id is unchanged but the
    /// display name changes (for example after a reconnect or a Steam persona rename).
    /// NetworkPeerId equality intentionally compares only Value, so cache keys need an
    /// explicit display-name check.
    /// </summary>
    public class PeerSnapshotRefreshTests
    {
        [Fact]
        public void CoopSessionState_RemotePeerIds_RefreshesDisplayNameForSameValue()
        {
            var session = new CoopSessionState();
            session.AddRemotePeer(new NetworkPeerId(2, "OldName"));

            session.AddRemotePeer(new NetworkPeerId(2, "NewName"));

            Assert.Single(session.RemotePeerIds);
            Assert.Equal("NewName", session.RemotePeerIds[0].DisplayName);
            Assert.Equal("NewName", session.RemotePeerId!.Value.DisplayName);
        }

        [Fact]
        public void PeerRegistry_UpsertAndSetState_RefreshDisplayNameForSameValue()
        {
            var registry = new PeerRegistry();
            registry.Upsert(new NetworkPeerId(2, "OldName"), PeerState.Ready, "Client");

            registry.Upsert(new NetworkPeerId(2, "NewName"), PeerState.Ready, "Client");

            Assert.Single(registry.Peers);
            Assert.Equal("NewName", registry.Peers[0].Peer.DisplayName);
            Assert.Single(registry.ReadyPeers);
            Assert.Equal("NewName", registry.ReadyPeers[0].DisplayName);

            registry.SetState(new NetworkPeerId(2, "NewerName"), PeerState.Authenticated);

            Assert.Equal("NewerName", registry.Peers[0].Peer.DisplayName);
            Assert.Empty(registry.ReadyPeers);
        }

        [Fact]
        public void LiteNetLibTransport_ConnectedPeers_RefreshesDisplayNameForSameValue()
        {
            var transport = new LiteNetLibTransport();
            try
            {
                var peerField = typeof(LiteNetLibTransport).GetField(
                    "_remotePeerId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(peerField);

                peerField!.SetValue(transport, new NetworkPeerId(2, "OldName"));
                Assert.Equal("OldName", transport.ConnectedPeers[0].DisplayName);

                peerField.SetValue(transport, new NetworkPeerId(2, "NewName"));
                Assert.Single(transport.ConnectedPeers);
                Assert.Equal("NewName", transport.ConnectedPeers[0].DisplayName);
            }
            finally
            {
                transport.Dispose();
            }
        }
    }
}
