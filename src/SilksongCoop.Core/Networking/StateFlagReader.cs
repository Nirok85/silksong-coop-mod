using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Reads the player animation/physics flags used by <see cref="TransformSyncPacket"/>.
    /// The game's controller state type is not available at compile time, so members are
    /// resolved once per runtime type and compiled into direct accessors. This used to live
    /// inside the Unity orchestrator; keeping it in Core makes the mapping testable without
    /// the game and keeps the orchestrator focused on scheduling.
    /// </summary>
    public sealed class StateFlagReader
    {
        private static readonly ConcurrentDictionary<Type, AccessorSet> Cache =
            new ConcurrentDictionary<Type, AccessorSet>();

        // Cache the factory delegate: a method-group conversion inside GetOrAdd would
        // allocate on every call on runtimes that do not cache static method groups.
        private static readonly Func<Type, AccessorSet> BuildAccessorSetFactory =
            BuildAccessorSet;

        private AccessorSet? _cachedAccessors;
        private Type? _cachedType;

        /// <summary>
        /// Builds the protocol flags for the supplied controller state. Unknown members are
        /// treated as false, matching the previous reflection helper.
        /// </summary>
        public ushort Build(object state)
        {
            if (state == null) return 0;

            var stateType = state.GetType();
            var accessors = _cachedAccessors;
            if (accessors == null || _cachedType != stateType)
            {
                accessors = Cache.GetOrAdd(stateType, BuildAccessorSetFactory);
                _cachedType = stateType;
                _cachedAccessors = accessors;
            }

            ushort flags = 0;
            if (GetBool(accessors.OnGround, state))
                flags |= TransformSyncPacket.FlagGrounded;
            if (GetBool(accessors.Dashing, state) ||
                GetBool(accessors.AirDashing, state) ||
                GetBool(accessors.ShadowDashing, state) ||
                GetBool(accessors.SuperDashing, state))
                flags |= TransformSyncPacket.FlagDashing;
            if (GetBool(accessors.WallSliding, state))
                flags |= TransformSyncPacket.FlagWallSliding;
            if (GetBool(accessors.Jumping, state) ||
                GetBool(accessors.DoubleJumping, state) ||
                GetBool(accessors.WallJumping, state))
                flags |= TransformSyncPacket.FlagJumping;
            if (GetBool(accessors.Falling, state))
                flags |= TransformSyncPacket.FlagFalling;
            if (GetBool(accessors.Attacking, state))
                flags |= TransformSyncPacket.FlagAttacking;
            if (GetBool(accessors.Invulnerable, state))
                flags |= TransformSyncPacket.FlagInvulnerable;
            if (GetBool(accessors.Swimming, state))
                flags |= TransformSyncPacket.FlagSwimming;

            return flags;
        }

        private static bool GetBool(Func<object, bool>? accessor, object instance)
        {
            return accessor != null && accessor(instance);
        }

        private static AccessorSet BuildAccessorSet(Type type)
        {
            return new AccessorSet
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

        private sealed class AccessorSet
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
                // scope: widening it could silently change which value a shadowing member
                // reports.
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
