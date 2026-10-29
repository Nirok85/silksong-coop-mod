using System;
using System.Buffers;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Networking;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Orchestrates network broadcasting, adaptive tick rate, interest management and
    /// packet batching. This extracts the transform/network hot-path from the main plugin.
    /// </summary>
    public class NetworkOrchestrator : ISyncOrchestrator
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IEventBus _eventBus;
        private readonly SilksongCoopPlugin _plugin;
        private readonly AdaptiveTickRate _tickRate;
        private readonly InterestManager _interestManager;
        private PacketBatcher? _packetBatcher;
        private NetworkPeerId _packetBatcherTarget;

        private float _transformSendAccumulator = 1f;
        private uint _transformTick;
        private Rigidbody2D? _cachedHeroBody;
        private GameObject? _cachedHeroBodyOwner;
        private readonly System.Collections.Generic.Dictionary<string, int> _clipHashCache = new System.Collections.Generic.Dictionary<string, int>(64);

        public float CurrentTickHz => _tickRate.CurrentTickHz;
        public int InterestTrackedCount => _interestManager.GetEntitiesInRange(System.Numerics.Vector2.Zero, float.MaxValue).Count;

        public NetworkOrchestrator(ISessionService sessionService, ITransportService transportService, IEventBus eventBus, SilksongCoopPlugin plugin)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _eventBus = eventBus;
            _plugin = plugin;
            _tickRate = new Core.Networking.AdaptiveTickRate(plugin.TickRateHzConfig?.Value ?? 30f);
            _interestManager = new InterestManager { DefaultRadius = 50f, Hysteresis = 8f };

            _eventBus.Subscribe<PeerConnectedEvent>(_ => OnPeerConnected());
            _eventBus.Subscribe<SessionEndedEvent>(_ => OnSessionEnded());
        }

        public void Initialize()
        {
            _tickRate.Reset();
        }

        public void OnSessionStarted(bool isHost)
        {
            _tickRate.Reset();
            // The batcher needs the remote peer id, which is only known once the handshake
            // completes; it is created lazily in BroadcastLocalTransform.
            _packetBatcher?.Flush();
            _packetBatcher = null;
        }

        public void OnSessionEnded()
        {
            _packetBatcher?.Flush();
            _packetBatcher = null;
            _interestManager.Clear();
            _tickRate.Reset();
        }

        public void Update()
        {
            var transport = _transportService.ActiveTransport;
            if (transport == null || !transport.IsConnected) return;

            _tickRate.Update(Time.unscaledTime, transport.RoundTripTimeMs, transport.PacketLossPercent);
            UpdateTransportDiagnostics(transport);
            BroadcastLocalTransform(Time.unscaledDeltaTime);
            _packetBatcher?.Update();
        }

        private void OnPeerConnected()
        {
            _packetBatcher?.Flush();
            _packetBatcher = null;
        }

        private void UpdateTransportDiagnostics(INetworkTransport transport)
        {
            var session = _sessionService.SessionState;
            if (session == null) return;

            int rtt = transport.RoundTripTimeMs;
            if (rtt > 0) session.RoundTripTimeMs = rtt;

            _plugin.MenuUI?.Diagnostics?.RecordPing(rtt);
            _plugin.MenuUI?.Diagnostics?.RecordLossPercent(transport.PacketLossPercent);
        }

        private void BroadcastLocalTransform(float deltaTime)
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true || !session.RemotePeerId.HasValue) return;
            if (session.Status == SessionStatus.Spectating || session.Status == SessionStatus.TeamWipe) return;

            var hero = HeroController.instance;
            if (hero == null || hero.gameObject == null) return;

            var state = hero.cState;
            if (state == null || state.dead || state.hazardDeath || state.hazardRespawning) return;

            float interval = _tickRate.CurrentInterval;
            _transformSendAccumulator += deltaTime;
            if (_transformSendAccumulator < interval) return;
            _transformSendAccumulator = Math.Min(_transformSendAccumulator - interval, interval);

            try
            {
                var position = hero.transform.position;
                float velocityX = 0f;
                float velocityY = 0f;

                if (_cachedHeroBody == null || _cachedHeroBodyOwner != hero.gameObject)
                {
                    _cachedHeroBody = hero.GetComponent<Rigidbody2D>();
                    _cachedHeroBodyOwner = hero.gameObject;
                }
                var body = _cachedHeroBody;
                if (body != null)
                {
                    Vector2 velocity = body.linearVelocity;
                    velocityX = velocity.x;
                    velocityY = velocity.y;
                }

                ushort flags = BuildStateFlags(state);
                int clipHash = 0;
                ushort normalizedTime = 0;
                var animator = hero.AnimCtrl?.animator;
                var clip = animator?.CurrentClip;
                if (clip != null)
                {
                    if (!_clipHashCache.TryGetValue(clip.name, out clipHash))
                    {
                        clipHash = AnimationClipHasher.ComputeHash(clip.name);
                        if (_clipHashCache.Count < 512) _clipHashCache[clip.name] = clipHash;
                    }
                    float duration = clip.Duration;
                    if (duration > 0f)
                        normalizedTime = AnimationClipHasher.EncodeNormalizedTime(animator!.ClipTimeSeconds / duration);
                }

                int localSceneHash = GameManager.instance != null ? GameManager.instance.sceneNameHash : 0;
                _transformTick++;
                var packet = new TransformSyncPacket
                {
                    Header = new PacketHeader(PacketType.TransformSync, 0, _transformTick, (uint)(Time.timeAsDouble * 1000.0)),
                    PositionX = position.x,
                    PositionY = position.y,
                    VelocityX = velocityX,
                    VelocityY = velocityY,
                    FacingDirection = (byte)(state.facingRight ? 1 : 0),
                    StateFlags = flags,
                    AnimationClipHash = clipHash,
                    NormalizedTime = normalizedTime,
                    SceneNameHash = localSceneHash
                };

                _interestManager.UpdateEntity(
                    transport.LocalPeerId.Value,
                    new System.Numerics.Vector2(position.x, position.y),
                    active: true);

                // Skip sending if remote peer is out of interest radius.
                if (session.RemotePeerId.HasValue &&
                    _plugin.CloneSpawner != null &&
                    _plugin.CloneSpawner.TryGetRemotePosition(session.RemotePeerId.Value, out float remoteX, out float remoteY))
                {
                    if (!_interestManager.IsInInterest(transport.LocalPeerId.Value, new System.Numerics.Vector2(position.x, position.y), session.RemotePeerId.Value.Value))
                    {
                        // Still send occasionally so partner sees us on the map
                        if ((_transformTick & 0x7) != 0) return;
                    }
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);

                    if (_packetBatcher == null || _packetBatcherTarget != session.RemotePeerId.Value)
                    {
                        _packetBatcher?.Flush();
                        _packetBatcher = new PacketBatcher(transport, session.RemotePeerId.Value);
                        _packetBatcherTarget = session.RemotePeerId.Value;
                    }

                    _packetBatcher.Enqueue(buffer, writer.Position, 0, DeliveryMethod.Unreliable);

                    session.RecordSent(writer.Position);
                    _plugin.MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[NetworkOrchestrator] Failed broadcasting local transform: {ex.Message}");
            }
        }

        private static ushort BuildStateFlags(object state)
        {
            var type = state.GetType();
            ushort flags = 0;
            if (GetBool(type, state, "onGround")) flags |= TransformSyncPacket.FlagGrounded;
            if (GetBool(type, state, "dashing") || GetBool(type, state, "airDashing") || GetBool(type, state, "shadowDashing") || GetBool(type, state, "superDashing"))
                flags |= TransformSyncPacket.FlagDashing;
            if (GetBool(type, state, "wallSliding")) flags |= TransformSyncPacket.FlagWallSliding;
            if (GetBool(type, state, "jumping") || GetBool(type, state, "doubleJumping") || GetBool(type, state, "wallJumping"))
                flags |= TransformSyncPacket.FlagJumping;
            if (GetBool(type, state, "falling")) flags |= TransformSyncPacket.FlagFalling;
            if (GetBool(type, state, "attacking")) flags |= TransformSyncPacket.FlagAttacking;
            if (GetBool(type, state, "invulnerable")) flags |= TransformSyncPacket.FlagInvulnerable;
            if (GetBool(type, state, "swimming")) flags |= TransformSyncPacket.FlagSwimming;
            return flags;
        }

        private static bool GetBool(System.Type type, object instance, string name)
        {
            var prop = type.GetProperty(name);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(instance)!;
            var field = type.GetField(name);
            if (field != null && field.FieldType == typeof(bool))
                return (bool)field.GetValue(instance)!;
            return false;
        }
    }
}
