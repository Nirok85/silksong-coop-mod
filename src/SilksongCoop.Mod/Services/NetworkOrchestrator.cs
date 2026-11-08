using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Orchestrates network broadcasting, adaptive tick rate and interest management.
    /// This extracts the transform/network hot-path from the main plugin.
    /// </summary>
    public class NetworkOrchestrator : ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IEventBus _eventBus;
        private readonly IPlayerCloneManager _cloneManager;
        private readonly AdaptiveTickRate _tickRate;
        private readonly InterestManager _interestManager;

        /// <summary>Current adaptive transform tick rate in Hz (visible in the status tab).</summary>
        public float CurrentTickHz => _tickRate.CurrentTickHz;

        /// <summary>
        /// Optional diagnostics sink, wired up after the Menu UI is created.
        /// </summary>
        public INetworkDiagnostics? Diagnostics { get; set; }

        private float _transformSendAccumulator = 1f;
        private float _lastFarTransformSend = float.NegativeInfinity;
        private uint _transformTick;
        private Rigidbody2D? _cachedHeroBody;
        private GameObject? _cachedHeroBodyOwner;
        private readonly System.Collections.Generic.Dictionary<string, int> _clipHashCache = new System.Collections.Generic.Dictionary<string, int>(64);
        private readonly Action<PeerConnectedEvent> _peerConnectedHandler;
        private readonly Action<SessionEndedEvent> _sessionEndedHandler;

        // Reused fallback recipient list for transports that report no connected peers.
        private readonly NetworkPeerId[] _singleRecipient = new NetworkPeerId[1];
        private NetworkPeerId? _singleRecipientPeer;

        public NetworkOrchestrator(
            ISessionService sessionService,
            ITransportService transportService,
            IEventBus eventBus,
            IPlayerCloneManager cloneManager,
            float defaultTickHz)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _eventBus = eventBus;
            _cloneManager = cloneManager;
            _tickRate = new Core.Networking.AdaptiveTickRate(defaultTickHz);
            _interestManager = new InterestManager { DefaultRadius = 50f, Hysteresis = 8f };

            _peerConnectedHandler = _ => OnPeerConnected();
            _sessionEndedHandler = _ => OnSessionEnded();
            _eventBus.Subscribe(_peerConnectedHandler);
            _eventBus.Subscribe(_sessionEndedHandler);
        }

        public void Dispose()
        {
            _eventBus.Unsubscribe(_peerConnectedHandler);
            _eventBus.Unsubscribe(_sessionEndedHandler);
        }

        public void Initialize()
        {
            _tickRate.Reset();
        }

        public void OnSessionStarted(bool isHost)
        {
            _tickRate.Reset();
        }

        public void OnSessionEnded()
        {
            _interestManager.Clear();
            _tickRate.Reset();
            _lastFarTransformSend = float.NegativeInfinity;
        }

        public void Update()
        {
            var transport = _transportService.ActiveTransport;
            if (transport == null || !transport.IsConnected) return;

            _tickRate.Update(Time.unscaledTime, transport.RoundTripTimeMs, transport.PacketLossPercent);
            UpdateTransportDiagnostics(transport);
            BroadcastLocalTransform(Time.unscaledDeltaTime);
        }

        private void OnPeerConnected()
        {
            _interestManager.Clear();
        }

        private void UpdateTransportDiagnostics(INetworkTransport transport)
        {
            var session = _sessionService.SessionState;
            if (session == null) return;

            int rtt = transport.RoundTripTimeMs;
            if (rtt > 0) session.RoundTripTimeMs = rtt;

            Diagnostics?.RecordPing(rtt);
            Diagnostics?.RecordLossPercent(transport.PacketLossPercent);
        }

        private void BroadcastLocalTransform(float deltaTime)
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true || !session.RemotePeerId.HasValue) return;
            if (session.Status == SessionStatus.Spectating || session.Status == SessionStatus.TeamWipe) return;

            var recipients = GetRecipients(transport, session);
            if (recipients.Count == 0) return;

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
                    // The receiver maps this timestamp onto its own Time.timeAsDouble render
                    // clock for interpolation, so it must stay in Unity's game-time domain:
                    // a pause or time-scale change freezes both sides together. Event packets
                    // use NetworkClock (monotonic real time) instead.
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

                // Area-of-interest gate. The manager must know where the remote peers are:
                // registering only the local player made every lookup for the remote id fail,
                // which throttled the stream to one packet in eight. The gate only applies
                // when at least one peer shares the scene: while players are apart the clone
                // keeps its last position from a different room, so a distance check would
                // throttle a reunion to the heartbeat rate and make the first seconds
                // visibly steppy.
                //
                // Evaluate every same-scene peer instead of breaking on the first one: with
                // three or more players, one distant peer must not suppress the stream to a
                // partner standing next to us.
                bool hasSameScenePeer = false;
                bool anyPeerInRange = false;
                for (int i = 0; i < recipients.Count; i++)
                {
                    var peer = recipients[i];
                    if (!_cloneManager.IsRemoteInSameScene(peer) ||
                        !_cloneManager.TryGetRemotePosition(peer, out float remoteX, out float remoteY))
                        continue;

                    hasSameScenePeer = true;
                    _interestManager.UpdateEntity(peer.Value, new System.Numerics.Vector2(remoteX, remoteY));

                    if (_interestManager.IsInInterest(
                            transport.LocalPeerId.Value,
                            new System.Numerics.Vector2(position.x, position.y),
                            peer.Value))
                    {
                        anyPeerInRange = true;
                    }
                }

                // All same-scene peers are far: keep a slow heartbeat so they still see us
                // when they come back, but bound it by time instead of tick parity.
                if (hasSameScenePeer &&
                    !anyPeerInRange &&
                    Time.unscaledTime - _lastFarTransformSend < 0.5f)
                {
                    return;
                }
                _lastFarTransformSend = Time.unscaledTime;

                byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);

                    transport.Send(
                        session.RemotePeerId.Value,
                        buffer,
                        0,
                        writer.Position,
                        DeliveryMethod.Unreliable,
                        NetworkChannels.Transform);

                    session.RecordSent(writer.Position);
                    Diagnostics?.RecordSent(writer.Position);

                    // N-player: single-peer transports report the same recipient list, so the
                    // extra loop costs nothing while multi-peer transports reach everyone.
                    for (int i = 0; i < recipients.Count; i++)
                    {
                        if (recipients[i] == session.RemotePeerId.Value) continue;

                        transport.Send(
                            recipients[i],
                            buffer,
                            0,
                            writer.Position,
                            DeliveryMethod.Unreliable,
                            NetworkChannels.Transform);

                        session.RecordSent(writer.Position);
                        Diagnostics?.RecordSent(writer.Position);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[NetworkOrchestrator] Failed broadcasting local transform: {ex.Message}");
            }
        }

        /// <summary>
        /// Remote peers that must receive transform updates. Multi-peer transports may
        /// report several peers; single-peer transports report exactly one.
        /// </summary>
        private IReadOnlyList<NetworkPeerId> GetRecipients(INetworkTransport transport, CoopSessionState session)
        {
            var connected = transport.ConnectedPeers;
            if (connected.Count > 0) return connected;
            if (!session.RemotePeerId.HasValue) return Array.Empty<NetworkPeerId>();

            // Reuse one single-element buffer: the fallback runs on every transform tick
            // and a fresh array here was a steady-state allocation.
            NetworkPeerId peer = session.RemotePeerId.Value;
            if (!_singleRecipientPeer.HasValue ||
                _singleRecipientPeer.Value.Value != peer.Value ||
                !string.Equals(
                    _singleRecipientPeer.Value.DisplayName,
                    peer.DisplayName,
                    StringComparison.Ordinal))
            {
                _singleRecipient[0] = peer;
                _singleRecipientPeer = peer;
            }
            return _singleRecipient;
        }

        // Reflection metadata lookups are expensive and this runs on every transform tick
        // (30-60 times per second with ~13 field probes each). Resolve each member once per
        // runtime state type and compile a direct getter; the fallback keeps the mod working
        // if the runtime rejects expression compilation (for example under a restrictive AOT
        // host), but still avoids the metadata lookup on every frame.
        //
        // The accessor set is strongly typed so the hot path does not do a string-keyed
        // dictionary lookup per flag. It is cached per runtime type (shared across all
        // orchestrator instances) and additionally on this instance, so a stable state type
        // costs one type comparison per tick instead of a ConcurrentDictionary lookup.
        private static readonly ConcurrentDictionary<Type, StateFlagAccessors> StateAccessors =
            new ConcurrentDictionary<Type, StateFlagAccessors>();

        // Cache the factory delegate once: a method-group conversion inside GetOrAdd could
        // allocate on every tick on runtimes that do not cache static method groups.
        private static readonly Func<Type, StateFlagAccessors> BuildAccessorSetFactory =
            BuildAccessorSet;

        private StateFlagAccessors? _cachedStateAccessors;
        private Type? _cachedStateType;

        private ushort BuildStateFlags(object state)
        {
            var stateType = state.GetType();
            var accessors = _cachedStateAccessors;
            if (accessors == null || _cachedStateType != stateType)
            {
                accessors = StateAccessors.GetOrAdd(stateType, BuildAccessorSetFactory);
                _cachedStateType = stateType;
                _cachedStateAccessors = accessors;
            }

            ushort flags = 0;
            if (GetBool(accessors.OnGround, state)) flags |= TransformSyncPacket.FlagGrounded;
            if (GetBool(accessors.Dashing, state) || GetBool(accessors.AirDashing, state) || GetBool(accessors.ShadowDashing, state) || GetBool(accessors.SuperDashing, state))
                flags |= TransformSyncPacket.FlagDashing;
            if (GetBool(accessors.WallSliding, state)) flags |= TransformSyncPacket.FlagWallSliding;
            if (GetBool(accessors.Jumping, state) || GetBool(accessors.DoubleJumping, state) || GetBool(accessors.WallJumping, state))
                flags |= TransformSyncPacket.FlagJumping;
            if (GetBool(accessors.Falling, state)) flags |= TransformSyncPacket.FlagFalling;
            if (GetBool(accessors.Attacking, state)) flags |= TransformSyncPacket.FlagAttacking;
            if (GetBool(accessors.Invulnerable, state)) flags |= TransformSyncPacket.FlagInvulnerable;
            if (GetBool(accessors.Swimming, state)) flags |= TransformSyncPacket.FlagSwimming;
            return flags;
        }

        private static bool GetBool(Func<object, bool>? accessor, object instance)
        {
            return accessor != null && accessor(instance);
        }

        private static StateFlagAccessors BuildAccessorSet(Type type)
        {
            return new StateFlagAccessors
            {
                OnGround = CreateBoolAccessor(type, "onGround"),
                Dashing = CreateBoolAccessor(type, "dashing"),
                AirDashing = CreateBoolAccessor(type, "airDashing"),
                ShadowDashing = CreateBoolAccessor(type, "shadowDashing"),
                SuperDashing = CreateBoolAccessor(type, "superDashing"),
                WallSliding = CreateBoolAccessor(type, "wallSliding"),
                Jumping = CreateBoolAccessor(type, "jumping"),
                DoubleJumping = CreateBoolAccessor(type, "doubleJumping"),
                WallJumping = CreateBoolAccessor(type, "wallJumping"),
                Falling = CreateBoolAccessor(type, "falling"),
                Attacking = CreateBoolAccessor(type, "attacking"),
                Invulnerable = CreateBoolAccessor(type, "invulnerable"),
                Swimming = CreateBoolAccessor(type, "swimming")
            };
        }

        private sealed class StateFlagAccessors
        {
            public Func<object, bool>? OnGround;
            public Func<object, bool>? Dashing;
            public Func<object, bool>? AirDashing;
            public Func<object, bool>? ShadowDashing;
            public Func<object, bool>? SuperDashing;
            public Func<object, bool>? WallSliding;
            public Func<object, bool>? Jumping;
            public Func<object, bool>? DoubleJumping;
            public Func<object, bool>? WallJumping;
            public Func<object, bool>? Falling;
            public Func<object, bool>? Attacking;
            public Func<object, bool>? Invulnerable;
            public Func<object, bool>? Swimming;
        }

        private static Func<object, bool>? CreateBoolAccessor(Type type, string name)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

            try
            {
                // Keep the original lookup order (property before field) and public-only
                // scope: the old reflection helper never read private members, and widening
                // it could silently change which value a shadowing member reports.
                var property = type.GetProperty(name, Flags);
                if (property != null && property.PropertyType == typeof(bool))
                    return CompilePropertyAccessor(type, property);

                var field = type.GetField(name, Flags);
                if (field != null && field.FieldType == typeof(bool))
                    return CompileFieldAccessor(type, field);
            }
            catch (AmbiguousMatchException)
            {
                // A type with two same-named public members is malformed for our purpose;
                // treat the flag as absent instead of aborting the transform send loop.
            }

            return null;
        }

        private static Func<object, bool> CompileFieldAccessor(Type type, FieldInfo field)
        {
            try
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var typedInstance = Expression.Convert(instance, type);
                var read = Expression.Field(typedInstance, field);
                return Expression.Lambda<Func<object, bool>>(read, instance).Compile();
            }
            catch
            {
                return instance => field.GetValue(instance) is bool value && value;
            }
        }

        private static Func<object, bool> CompilePropertyAccessor(Type type, PropertyInfo property)
        {
            try
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var typedInstance = Expression.Convert(instance, type);
                var read = Expression.Property(typedInstance, property);
                return Expression.Lambda<Func<object, bool>>(read, instance).Compile();
            }
            catch
            {
                return instance => property.GetValue(instance) is bool value && value;
            }
        }
    }
}
