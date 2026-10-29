using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.DI
{
    /// <summary>
    /// Simple service container for dependency injection.
    /// </summary>
    public interface IServiceContainer
    {
        void Register<TInterface, TImplementation>() where TImplementation : class, TInterface;
        void Register<TService>(TService instance) where TService : class;
        void RegisterSingleton<TService>(Func<IServiceProvider, TService> factory) where TService : class;
        void RegisterSingleton<TInterface, TService>(Func<IServiceProvider, TService> factory) where TService : class, TInterface;
        TService Resolve<TService>() where TService : class;
        object Resolve(Type serviceType);
        bool TryResolve<TService>(out TService? service) where TService : class;
    }

    public interface IServiceProvider
    {
        TService Resolve<TService>() where TService : class;
        object Resolve(Type serviceType);
    }
}
