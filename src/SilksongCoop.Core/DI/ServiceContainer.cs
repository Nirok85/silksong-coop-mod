using System;
using System.Collections.Generic;
using System.Threading;

namespace SilksongCoop.Core.DI
{
    /// <summary>
    /// Lightweight service container implementation.
    /// </summary>
    public class ServiceContainer : IServiceContainer, IServiceProvider
    {
        private enum RegistrationType
        {
            Instance,
            SingletonFactory,
            Transient
        }

        private class Registration
        {
            public RegistrationType Type;
            public object? Instance;
            public Func<IServiceProvider, object>? Factory;
            public Type? ImplementationType;
            public int CreatingThread;
        }

        private readonly Dictionary<Type, Registration> _registrations = new Dictionary<Type, Registration>();
        private readonly object _lock = new object();
        private readonly AsyncLocal<HashSet<Type>?> _resolutionPath = new();
        private readonly Dictionary<int, int> _waitingFor = new();

        public void Register<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            lock (_lock)
            {
                _registrations[typeof(TInterface)] = new Registration
                {
                    Type = RegistrationType.Transient,
                    ImplementationType = typeof(TImplementation)
                };
            }
        }

        public void Register<TService>(TService instance) where TService : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (_lock)
            {
                _registrations[typeof(TService)] = new Registration
                {
                    Type = RegistrationType.Instance,
                    Instance = instance
                };
            }
        }

        public void RegisterSingleton<TService>(Func<IServiceProvider, TService> factory) where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _registrations[typeof(TService)] = new Registration
                {
                    Type = RegistrationType.SingletonFactory,
                    Factory = sp => factory(sp)
                };
            }
        }

        public void RegisterSingleton<TInterface, TService>(Func<IServiceProvider, TService> factory) where TService : class, TInterface
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _registrations[typeof(TInterface)] = new Registration
                {
                    Type = RegistrationType.SingletonFactory,
                    Factory = sp => factory(sp)
                };
            }
        }

        public TService Resolve<TService>() where TService : class
        {
            var result = Resolve(typeof(TService));
            if (result is not TService typed)
                throw new InvalidOperationException($"Service of type {typeof(TService).Name} not found or wrong type");
            return typed;
        }

        public object Resolve(Type serviceType)
        {
            Registration? registration;
            lock (_lock)
            {
                if (!_registrations.TryGetValue(serviceType, out registration))
                    throw new InvalidOperationException($"Service of type {serviceType.Name} not registered");
            }

            var previousPath = _resolutionPath.Value;
            if (previousPath?.Contains(serviceType) == true)
                throw new InvalidOperationException($"Circular dependency for {serviceType.Name}");
            var path = previousPath == null ? new HashSet<Type>() : new HashSet<Type>(previousPath);
            path.Add(serviceType);
            _resolutionPath.Value = path;
            try
            {
                return registration.Type switch
                {
                    RegistrationType.Instance => registration.Instance!,
                    RegistrationType.SingletonFactory => GetOrCreateSingleton(registration, serviceType),
                    RegistrationType.Transient => CreateInstance(registration.ImplementationType!),
                    _ => throw new InvalidOperationException($"Unknown registration type for {serviceType.Name}")
                };
            }
            finally
            {
                _resolutionPath.Value = previousPath;
            }
        }

        /// <summary>
        /// Creates a singleton once, serialising concurrent resolutions. Network callbacks
        /// can resolve services off the main thread; the previous null-check race created
        /// two instances and let one of them silently replace the other.
        /// </summary>
        private object GetOrCreateSingleton(Registration registration, Type serviceType)
        {
            int thread = Environment.CurrentManagedThreadId;
            lock (_lock)
            {
                while (registration.CreatingThread != 0)
                {
                    int owner = registration.CreatingThread;
                    for (int current = owner; current != 0;)
                    {
                        if (current == thread)
                            throw new InvalidOperationException($"Circular dependency for {serviceType.Name}");
                        _waitingFor.TryGetValue(current, out current);
                    }
                    _waitingFor[thread] = owner;
                    try { Monitor.Wait(_lock); }
                    finally { _waitingFor.Remove(thread); }
                }

                if (registration.Instance != null) return registration.Instance;
                registration.CreatingThread = thread;
            }

            object? instance = null;
            try
            {
                instance = registration.Factory!(this)
                    ?? throw new InvalidOperationException($"Singleton factory for {serviceType.Name} returned null");
                return instance;
            }
            finally
            {
                lock (_lock)
                {
                    registration.Instance = instance;
                    registration.CreatingThread = 0;
                    Monitor.PulseAll(_lock);
                }
            }
        }

        public bool TryResolve<TService>(out TService? service) where TService : class
        {
            service = null;
            Registration? registration;
            lock (_lock)
            {
                if (!_registrations.TryGetValue(typeof(TService), out registration))
                    return false;
            }

            try
            {
                var result = Resolve(typeof(TService));
                service = result as TService;
                return service != null;
            }
            catch
            {
                return false;
            }
        }

        private object CreateInstance(Type implementationType)
        {
            var constructors = implementationType.GetConstructors();
            foreach (var ctor in constructors)
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0)
                    return Activator.CreateInstance(implementationType)!;

                var args = new object[parameters.Length];
                bool canResolve = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    try
                    {
                        args[i] = Resolve(parameters[i].ParameterType);
                    }
                    catch
                    {
                        canResolve = false;
                        break;
                    }
                }
                if (canResolve)
                    return ctor.Invoke(args);
            }

            return Activator.CreateInstance(implementationType)!;
        }
    }
}
