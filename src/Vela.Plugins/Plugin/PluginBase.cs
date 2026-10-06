using System;
using System.Diagnostics;
using Vela.Context;
using Vela.DI;
using Vela.Retry;
using Microsoft.Xrm.Sdk;

namespace Vela.Plugin
{
    /// <summary>
    /// Base Dataverse plugin implementation with standard pipeline safety and diagnostics.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PluginBase"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class PluginBase(string unsecureConfig, string secureConfig, int maxDepth = 8) : IPlugin
    {
        /// <summary>
        /// Gets unsecure configuration.
        /// </summary>
        protected string UnsecureConfig { get; } = unsecureConfig ?? string.Empty;

        /// <summary>
        /// Gets secure configuration.
        /// </summary>
        protected string SecureConfig { get; } = secureConfig ?? string.Empty;

        /// <summary>
        /// Gets max execution depth threshold.
        /// </summary>
        protected int MaxDepth { get; } = maxDepth < 1 ? 1 : maxDepth;

        /// <summary>
        /// Main plugin entry point.
        /// </summary>
        /// <param name="serviceProvider">Runtime service provider.</param>
        public void Execute(IServiceProvider serviceProvider)
        {
            LocalPluginContext localContext = new LocalPluginContext(serviceProvider ?? throw new InvalidPluginExecutionException(nameof(serviceProvider)), GetType().FullName);

            // Assembly-wide shared services first (one [assembly: VelaServiceRegistrar] for all plugins/Custom APIs
            // in the assembly), then the per-plugin ConfigureServices - so a plugin can still override a shared
            // registration (the resolver is last-registration-wins).
            AssemblyServiceRegistrars.Resolve(GetType())?.Register(localContext.Resolver, localContext);
            ConfigureServices(localContext.Resolver, localContext);

            Stopwatch stopwatch = Stopwatch.StartNew();
            localContext.Trace(
                "Executing {0}. Message={1}, Stage={2}, Mode={3}, Depth={4}",
                GetType().FullName,
                localContext.MessageName,
                localContext.Stage,
                localContext.Mode,
                localContext.Depth);
            localContext.TraceExecutionSnapshot("start");

            try
            {
                EnforceDepth(localContext);
                ExecuteDataversePlugin(localContext);
                localContext.Trace("Execution completed in {0} ms.", stopwatch.ElapsedMilliseconds);
                localContext.TraceExecutionSnapshot("success");
            }
            catch (InvalidPluginExecutionException)
            {
                localContext.TraceExecutionSnapshot("invalid-plugin-exception");
                throw;
            }
            catch (PluginBusinessException businessException)
            {
                localContext.Trace("Business exception: {0}", businessException.Message);
                localContext.TraceExecutionSnapshot("business-exception");
                throw new InvalidPluginExecutionException(businessException.Message);
            }
            catch (Exception ex)
            {
                localContext.Trace("Unhandled exception: {0}", ex);
                localContext.TraceExecutionSnapshot("unhandled-exception");
                // Surface the real cause (type + message) to the caller instead of a generic "an error occurred":
                // the caller only ever sees the InvalidPluginExecutionException message, and a generic one hides
                // the actual failure (for example a SerializationException from passing an early-bound entity to a
                // service call). The full stack trace stays in the plugin trace log (the "Unhandled exception"
                // line above) and in the inner exception passed here.
                throw new InvalidPluginExecutionException(
                    $"Unhandled exception in plugin '{GetType().FullName}': {ex.GetType().Name}: {ex.Message}", ex);
            }
            finally
            {
                // Dispose the transient IDisposable services the resolver created for this execution (scoped and
                // platform-owned services are left untouched). Runs on both the success and failure paths.
                localContext.Resolver.Dispose();
            }
        }

        /// <summary>
        /// Allows derived plugins to register custom services.
        /// </summary>
        /// <param name="services">Execution resolver.</param>
        /// <param name="context">Execution context.</param>
        protected virtual void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
        }

        /// <summary>
        /// Whether the runtime writes a one-line trace naming the handler it dispatches, complementing the
        /// plugin-level "Executing ..." line with the specific step/Custom API handler that fired (a plugin can
        /// register several). On by default; override to return <c>false</c> to silence it. The platform Plugin
        /// Trace Log already gates whether the line is persisted, so the cost is negligible.
        /// </summary>
        protected virtual bool AutoTraceHandlerEntry => true;

        // Writes the handler-entry trace from a dispatch path (next to SetCurrentHandler), when enabled and the
        // handler has a source name. A lambda / local-function handler has only a compiler-generated name (it
        // contains '<', which is illegal in a source identifier), which would be noise in the trace log, so it is
        // skipped - the trace names real handlers only.
        private protected void TraceHandlerEntry(ILocalPluginContext context, string? handlerName)
        {
            if (AutoTraceHandlerEntry && !string.IsNullOrEmpty(handlerName) && handlerName!.IndexOf('<') < 0)
            {
                context.Trace("Entering handler '{0}' for message '{1}'.", handlerName, context.MessageName);
            }
        }

        /// <summary>
        /// Optional sandbox-safe retry wrapper for organization service operations. Retries only safe-to-replay
        /// throttling faults; timeouts are not retried (a timed-out write may already have committed). See
        /// <see cref="RetryPolicy"/> for the idempotency contract.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Operation to execute.</param>
        /// <returns>Operation result.</returns>
        protected static T ExecuteWithRetry<T>(Func<T> operation)
        {
            return RetryPolicy.Execute(operation);
        }

        /// <summary>
        /// Retry wrapper that also retries timeouts. Use only for an idempotent operation (a read, or a write that
        /// is safe to replay); a non-idempotent write may already have committed before a timeout surfaced.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Idempotent operation to execute.</param>
        /// <returns>Operation result.</returns>
        protected static T ExecuteIdempotentWithRetry<T>(Func<T> operation)
        {
            return RetryPolicy.Execute(operation, retryOnTimeout: true);
        }

        /// <summary>
        /// Executes plugin business logic.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected abstract void ExecuteDataversePlugin(ILocalPluginContext ctx);

        private void EnforceDepth(ILocalPluginContext context)
        {
            if (context.Depth <= MaxDepth)
            {
                return;
            }

            context.Trace("Depth guard triggered. Depth={0}, MaxDepth={1}", context.Depth, MaxDepth);
            throw new InvalidPluginExecutionException("Plugin execution stopped to prevent recursion.");
        }
    }
}
