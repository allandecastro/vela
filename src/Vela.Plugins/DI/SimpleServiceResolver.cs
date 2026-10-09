using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Vela.Caching;
using Vela.Context;
using Vela.Localization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace Vela.DI
{
    /// <summary>
    /// Lightweight per-execution resolver with transient and scoped lifetimes.
    /// </summary>
    /// <remarks>
    /// This resolver is created per plugin execution and is <b>not</b> thread-safe: the Dataverse pipeline invokes a
    /// plugin on a single thread, so registration and resolution are expected to happen on that one thread. Do not
    /// share a resolver across threads or resolve from a background thread. A self-referential or cyclic factory is
    /// detected during resolution and reported as an <see cref="InvalidPluginExecutionException"/> rather than a
    /// <see cref="StackOverflowException"/>.
    /// </remarks>
    public sealed class SimpleServiceResolver : IPluginServiceResolver, IDisposable
    {
        private static readonly PluginCacheService GlobalCacheService = new PluginCacheService();
        private readonly Dictionary<Type, Func<IPluginServiceResolver, object>> _factories = new Dictionary<Type, Func<IPluginServiceResolver, object>>();
        private readonly HashSet<Type> _scopedRegistrations = new HashSet<Type>();
        private readonly Dictionary<Type, object> _scopedInstances = new Dictionary<Type, object>();
        // IDisposable transients this resolver created, disposed when the resolver is disposed at the end of the
        // execution. Only transients are tracked: scoped registrations are platform/caller-owned (the default
        // context/service/tracing registrations are scoped) and must not be disposed here.
        private readonly List<IDisposable> _transientDisposables = new List<IDisposable>();
        // Types currently being resolved on this (per-execution, single-threaded) resolver, to detect a factory that
        // resolves its own type (A -> A) or a cycle (A -> B -> A) and fail with a clear message instead of a
        // StackOverflowException that cannot be caught and would tear down the worker.
        private readonly HashSet<Type> _resolving = new HashSet<Type>();

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
            foreach (KeyValuePair<Type, Func<object>> registration in DefaultServices(context))
            {
                Func<object> create = registration.Value;
                resolver.Add(registration.Key, new Func<IPluginServiceResolver, object>(_ => create()), isScoped: true);
            }

            return resolver;
        }

        /// <summary>
        /// The default per-execution services, shared by <see cref="CreateDefault"/> and
        /// <see cref="MicrosoftDiExtensions.AddPluginRuntime"/> so both containers resolve the same set.
        /// <see cref="IOrganizationService"/> is the current user's <see cref="ILocalPluginContext.Service"/>; SYSTEM is
        /// never resolved implicitly (take <see cref="ILocalPluginContext"/> and use <c>SystemService</c>).
        /// </summary>
        internal static IEnumerable<KeyValuePair<Type, Func<object>>> DefaultServices(ILocalPluginContext context)
        {
            yield return Scoped<ILocalPluginContext>(() => context);
            yield return Scoped(() => context.PluginExecutionContext);
            yield return Scoped(() => context.Service);
            yield return Scoped(() => context.TracingService);
            yield return Scoped<ILogger>(() => context.Logger ?? NullPluginTelemetryLogger.Instance);
            // The injected cache is the organization-scoped view of the process-wide cache: user keys cannot collide
            // across organizations, and Clear() only drops this organization's entries (the framework's own entries
            // included, since the built-in services go through the same view).
            IPluginCacheService? scopedCache = null;
            IPluginCacheService ScopedCache() => scopedCache ??= new OrganizationScopedCacheService(GlobalCacheService, context.OrganizationId);
            yield return Scoped<IPluginCacheService>(ScopedCache);
            yield return Scoped<IEnvironmentVariableReader>(() => new EnvironmentVariableReader(context, ScopedCache()));
            yield return Scoped<IMessageService>(() => new MessageService(context, ScopedCache()));
            if (context.ManagedIdentityService != null)
            {
                yield return Scoped(() => context.ManagedIdentityService);
            }
        }

        private static KeyValuePair<Type, Func<object>> Scoped<TService>(Func<TService> factory) where TService : class
        {
            return new KeyValuePair<Type, Func<object>>(typeof(TService), () => factory());
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

            if (!_resolving.Add(serviceType))
            {
                throw new InvalidPluginExecutionException(
                    "Circular service dependency detected while resolving " + serviceType.FullName + " (chain: " + string.Join(" -> ", _resolving) + ").");
            }

            try
            {
                object service = factory(this);
                if (_scopedRegistrations.Contains(serviceType))
                {
                    _scopedInstances[serviceType] = service;
                }

                return service;
            }
            finally
            {
                _resolving.Remove(serviceType);
            }
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

                object resolved = service ?? throw new InvalidPluginExecutionException("Service factory returned null for " + serviceType.FullName);
                if (!isScoped && resolved is IDisposable disposable)
                {
                    _transientDisposables.Add(disposable);
                }

                return resolved;
            };

            // Re-registration must not leak the previous lifetime: drop any cached scoped instance and align the
            // scoped-registration set with the new lifetime, so a scoped -> transient re-registration stops caching
            // (and does not keep handing out the stale scoped instance).
            _scopedInstances.Remove(serviceType);
            if (isScoped)
            {
                _scopedRegistrations.Add(serviceType);
            }
            else
            {
                _scopedRegistrations.Remove(serviceType);
            }

            return this;
        }

        /// <summary>
        /// Disposes the transient <see cref="IDisposable"/> services this resolver created. Scoped and
        /// platform-owned services are left untouched. A faulty <c>Dispose</c> is swallowed so it cannot mask the
        /// plugin result.
        /// </summary>
        public void Dispose()
        {
            for (int i = _transientDisposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _transientDisposables[i].Dispose();
                }
                catch (Exception)
                {
                    // A service's own Dispose failure must not surface as the plugin's outcome.
                }
            }

            _transientDisposables.Clear();
        }
    }
}
