using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.Events
{
    /// <summary>
    /// Event bus implementation with thread-safe subscriptions.
    /// </summary>
    public class EventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _subscribers = new Dictionary<Type, List<Delegate>>();
        private readonly object _lock = new object();

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (_lock)
            {
                var type = typeof(TEvent);
                if (!_subscribers.TryGetValue(type, out var list))
                {
                    list = new List<Delegate>();
                    _subscribers[type] = list;
                }
                list.Add(handler);
            }
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (_lock)
            {
                var type = typeof(TEvent);
                if (_subscribers.TryGetValue(type, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                        _subscribers.Remove(type);
                }
            }
        }

        public void Publish<TEvent>(TEvent @event) where TEvent : class
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            List<Delegate>? handlers;
            lock (_lock)
            {
                var type = typeof(TEvent);
                if (!_subscribers.TryGetValue(type, out handlers))
                    return;
                handlers = new List<Delegate>(handlers);
            }

            foreach (var handler in handlers)
            {
                try
                {
                    ((Action<TEvent>)handler)(@event);
                }
                catch (Exception ex)
                {
                    CoopLog.Warn($"[EventBus] Exception in handler for {typeof(TEvent).Name}: {ex.Message}");
                }
            }
        }
    }
}
