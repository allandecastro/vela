using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Vela.Caching;
using Vela.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;

namespace Vela.DI
{
    /// <summary>
    /// Lightweight per-execution resolver with transient and scoped lifetimes.
    /// </summary>
    public sealed class SimpleServiceResolver : IPluginServiceResolver
    {
        private static readonly PluginCacheService GlobalCacheService = new PluginCacheService();
        private readonly Dictionary<Type, Func<IPluginServiceResolver, object>> _factories = new Dictionary<Type, Func<IPluginServiceResolver, object>>();
        private readonly HashSet<Type> _scopedRegistrations = new HashSet<Type>();
        private readonly Dictionary<Type, object> _scopedInstances = new Dictionary<Type, object>();

        /// <summary>
        /// Creates default resolver registrations.
        /// </summary>
        /// <param name="context">Local plugin context.</param>
        /// <returns>Configured resolver.</returns>
        public static SimpleServiceResolver CreateDefault(ILocalPluginContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            SimpleServiceResolver resolver = new();
            resolver.AddScoped<ILocalPluginContext>(_ => context);
            resolver.AddScoped(_ => context.PluginExecutionContext);
            resolver.AddScoped(_ => context.UserService);
            resolver.AddScoped(_ => context.TracingService);
            resolver.AddScoped(_ => context.Logger ?? NullLogger.Instance);
            resolver.AddScoped<Func<Guid?, IOrganizationService>>(_ => userId => context.CreateOrganizationService(userId));
            resolver.AddScoped<IPluginCacheService>(_ => GlobalCacheService);
            resolver.AddScoped<IEnvironmentVariableReader>(_ => new EnvironmentVariableReader(context, GlobalCacheService));
            if (context.ManagedIdentityService != null)
            {
                resolver.AddScoped<IManagedIdentityService>(_ => context.ManagedIdentityService);
            }
            return resolver;
        }

        /// <summary>
        /// Registers scoped service.
        /// </summary>
        /// <typeparam name="TService">Service type.</typeparam>
        /// <param name="factory">Service factory.</param>
        /// <returns>Resolver instance.</returns>
        public SimpleServiceResolver AddScoped<TService>(Func<IPluginServiceResolver, TService> factory) where TService : class
        {
            return Add(typeof(TService), factory, isScoped: true);
        }

        /// <summary>
        /// Registers transient service.
        /// </summary>
        /// <typeparam name="TService">Service type.</typeparam>
        /// <param name="factory">Service factory.</param>
        /// <returns>Resolver instance.</returns>
        public SimpleServiceResolver AddTransient<TService>(Func<IPluginServiceResolver, TService> factory) where TService : class
        {
            return Add(typeof(TService), factory, isScoped: false);
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType)
        {
            if (_scopedInstances.TryGetValue(serviceType, out object? scopedInstance))
            {
                return scopedInstance;
            }

            if (!_factories.TryGetValue(serviceType, out Func<IPluginServiceResolver, object>? factory))
            {
                return null;
            }

            object service = factory(this);
            if (_scopedRegistrations.Contains(serviceType))
            {
                _scopedInstances[serviceType] = service;
            }

            return service;
        }

        /// <inheritdoc />
        public T GetRequiredService<T>() where T : class
        {
            object? service = GetService(typeof(T));
            if (service == null)
            {
                throw new InvalidPluginExecutionException("Service not registered: " + typeof(T).FullName);
            }

            return (T)service;
        }

        private SimpleServiceResolver Add(Type serviceType, Delegate factory, bool isScoped)
        {
            _factories[serviceType] = provider =>
            {
                object? service;
                try
                {
                    service = factory.DynamicInvoke(provider);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    // DynamicInvoke wraps a throwing factory in TargetInvocationException; surface the real
                    // exception (with its stack) instead, like HandlerInvoker does for handler methods.
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw; // unreachable
                }

                return service ?? throw new InvalidPluginExecutionException("Service factory returned null for " + serviceType.FullName);
            };
            if (isScoped)
            {
                _scopedRegistrations.Add(serviceType);
            }

            return this;
        }
    }
}
