using System;
using System.Collections.Generic;

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
        }

        private readonly Dictionary<Type, Registration> _registrations = new Dictionary<Type, Registration>();
        private readonly object _lock = new object();

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

            return registration.Type switch
            {
                RegistrationType.Instance => registration.Instance!,
                RegistrationType.SingletonFactory => registration.Instance ?? (registration.Instance = registration.Factory!(this)!),
                RegistrationType.Transient => CreateInstance(registration.ImplementationType!),
                _ => throw new InvalidOperationException($"Unknown registration type for {serviceType.Name}")
            };
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
