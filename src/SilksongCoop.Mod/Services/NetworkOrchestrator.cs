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
    public class NetworkOrchestrator : ISyncOrchestrator
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IEventBus _eventBus;
        private readonly IPlayerCloneManager _cloneManager;
        private readonly AdaptiveTickRate _tickRate;
        private readonly InterestManager _interestManager;

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
                // when a peer shares the scene: while players are apart the clone keeps its
                // last position from a different room, so a distance check would throttle a
                // reunion to the heartbeat rate and make the first seconds visibly steppy.
                // The gate is evaluated for the first same-scene peer only; the recipient
                // list decides actual delivery, so one distant peer can never hide the
                // stream from another one standing next to us.
                for (int i = 0; i < recipients.Count; i++)
                {
                    var peer = recipients[i];
                    if (!_cloneManager.IsRemoteInSameScene(peer) ||
                        !_cloneManager.TryGetRemotePosition(peer, out float remoteX, out float remoteY))
                        continue;

                    _interestManager.UpdateEntity(peer.Value, new System.Numerics.Vector2(remoteX, remoteY));

                    bool inRange = _interestManager.IsInInterest(
                        transport.LocalPeerId.Value,
                        new System.Numerics.Vector2(position.x, position.y),
                        peer.Value);

                    // Out of range: keep a slow heartbeat so the partner still sees us when
                    // they come back, but bound it by time instead of tick parity.
                    if (!inRange && Time.unscaledTime - _lastFarTransformSend < 0.5f)
                        return;
                    break;
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
        private static IReadOnlyList<NetworkPeerId> GetRecipients(INetworkTransport transport, CoopSessionState session)
        {
            var connected = transport.ConnectedPeers;
            if (connected.Count > 0) return connected;
            return session.RemotePeerId.HasValue
                ? new[] { session.RemotePeerId.Value }
                : System.Array.Empty<NetworkPeerId>();
        }

        // Reflection metadata lookups are expensive and this runs on every transform tick
        // (30-60 times per second with ~13 field probes each). Resolve each member once per
        // runtime state type and compile a direct getter; the fallback keeps the mod working
        // if the runtime rejects expression compilation (for example under a restrictive AOT
        // host), but still avoids the metadata lookup on every frame.
        private static readonly string[] StateFlagNames =
        {
            "onGround",
            "dashing", "airDashing", "shadowDashing", "superDashing",
            "wallSliding",
            "jumping", "doubleJumping", "wallJumping",
            "falling",
            "attacking",
            "invulnerable",
            "swimming"
        };

        private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Func<object, bool>>> StateAccessors =
            new ConcurrentDictionary<Type, IReadOnlyDictionary<string, Func<object, bool>>>();

        // Cache the factory delegate once: a method-group conversion inside GetOrAdd could
        // allocate on every tick on runtimes that do not cache static method groups.
        private static readonly Func<Type, IReadOnlyDictionary<string, Func<object, bool>>> BuildAccessorMapFactory =
            BuildAccessorMap;

        private static ushort BuildStateFlags(object state)
        {
            var accessors = StateAccessors.GetOrAdd(state.GetType(), BuildAccessorMapFactory);
            ushort flags = 0;
            if (GetBool(accessors, state, "onGround")) flags |= TransformSyncPacket.FlagGrounded;
            if (GetBool(accessors, state, "dashing") || GetBool(accessors, state, "airDashing") || GetBool(accessors, state, "shadowDashing") || GetBool(accessors, state, "superDashing"))
                flags |= TransformSyncPacket.FlagDashing;
            if (GetBool(accessors, state, "wallSliding")) flags |= TransformSyncPacket.FlagWallSliding;
            if (GetBool(accessors, state, "jumping") || GetBool(accessors, state, "doubleJumping") || GetBool(accessors, state, "wallJumping"))
                flags |= TransformSyncPacket.FlagJumping;
            if (GetBool(accessors, state, "falling")) flags |= TransformSyncPacket.FlagFalling;
            if (GetBool(accessors, state, "attacking")) flags |= TransformSyncPacket.FlagAttacking;
            if (GetBool(accessors, state, "invulnerable")) flags |= TransformSyncPacket.FlagInvulnerable;
            if (GetBool(accessors, state, "swimming")) flags |= TransformSyncPacket.FlagSwimming;
            return flags;
        }

        private static bool GetBool(IReadOnlyDictionary<string, Func<object, bool>> accessors, object instance, string name)
        {
            return accessors.TryGetValue(name, out var accessor) && accessor(instance);
        }

        private static IReadOnlyDictionary<string, Func<object, bool>> BuildAccessorMap(Type type)
        {
            var accessors = new Dictionary<string, Func<object, bool>>(StateFlagNames.Length, StringComparer.Ordinal);

            foreach (string name in StateFlagNames)
            {
                var accessor = CreateBoolAccessor(type, name);
                if (accessor != null)
                    accessors[name] = accessor;
            }

            return accessors;
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
