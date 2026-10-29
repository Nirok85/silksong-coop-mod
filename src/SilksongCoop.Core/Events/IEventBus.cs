using System;

namespace SilksongCoop.Core.Events
{
    /// <summary>
    /// Central event bus for decoupled communication between services.
    /// </summary>
    public interface IEventBus
    {
        void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
        void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
        void Publish<TEvent>(TEvent @event) where TEvent : class;
    }

    /// <summary>
    /// Base class for all events.
    /// </summary>
    public abstract class GameEvent
    {
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }
}
