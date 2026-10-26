using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.Clones
{
    /// <summary>
    /// Represents the full interpolated runtime state of an active remote player clone.
    /// </summary>
    public struct RemoteCloneState
    {
        public NetworkPeerId PeerId;
        public bool IsPlayer2;
        public float PositionX;
        public float PositionY;
        public float VelocityX;
        public float VelocityY;
        public bool FacingRight;
        public ushort StateFlags;
        public int ActiveClipHash;
        public float ClipNormalizedTime;
        public bool IsDashActive;
        public float DashTimeRemaining;

        public bool IsGrounded => (StateFlags & Protocol.Packets.TransformSyncPacket.FlagGrounded) != 0;
        public bool IsDashing => (StateFlags & Protocol.Packets.TransformSyncPacket.FlagDashing) != 0 || IsDashActive;
    }
}
