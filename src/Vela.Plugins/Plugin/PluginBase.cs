using System;
using System.Diagnostics;
using System.ServiceModel;
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
    /// <param name="maxDepth">
    /// Depth guard threshold (defaults to <see cref="DefaultMaxDepth"/>). The guard is a last-resort backstop, not
    /// recursion prevention: see <see cref="DefaultMaxDepth"/>.
    /// </param>
    public abstract class PluginBase(string unsecureConfig, string secureConfig, int maxDepth = PluginBase.DefaultMaxDepth) : IPlugin
    {
        /// <summary>
        /// The default depth guard threshold shared by every Vela base class: an execution whose
        /// <see cref="ILocalPluginContext.Depth"/> exceeds it is stopped. It matches the Dataverse default depth limit.
        /// </summary>
        /// <remarks>
        /// At this default the guard only stops, with a clearer trace, a chain the platform would abort anyway: Dataverse
        /// treats a chain that reaches depth 8 within an hour as an infinite loop. Microsoft advises against business
        /// logic that depends on a specific <c>Depth</c> value, because the same operation can arrive through a deeper
        /// code path after a configuration change. A lower value can therefore reject a legitimate deep chain (a
        /// workflow, a Power Automate flow or another plugin calling in). Prevent recursion with <c>FilterOn</c>, a
        /// change check (<c>HasChanged</c>) or a shared variable flag instead, and keep this guard as a backstop.
        /// </remarks>
        public const int DefaultMaxDepth = 8;

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
            if (serviceProvider == null)
            {
                throw new InvalidPluginExecutionException(nameof(serviceProvider));
            }

            // Building the context resolves the platform services. A failure here has no context to trace through
            // yet, so trace it best-effort straight to the platform tracing service and surface it as an
            // InvalidPluginExecutionException like any other failure (never a raw framework exception), with a
            // generic message: the details go to the trace log only.
            LocalPluginContext localContext;
            try
            {
                localContext = new LocalPluginContext(serviceProvider, GetType().FullName);
            }
            catch (Exception ex)
            {
                TryTraceStartupFailure(serviceProvider, ex);
                if (ex is InvalidPluginExecutionException)
                {
                    throw;
                }

                throw new InvalidPluginExecutionException(
                    "An unexpected error occurred while building the plug-in execution context.", ex);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            TraceSnapshotDetail snapshotDetail = AutoSnapshotDetail;
            localContext.TraceServiceCalls = AutoTraceServiceCalls;
            try
            {
                // Service registration runs inside the try, so a throwing registrar or ConfigureServices is traced,
                // wrapped like a handler failure, and the resolver is still disposed in the finally below.
                // Assembly-wide shared services first (one [assembly: VelaServiceRegistrar] for all plugins/Custom
                // APIs in the assembly), then the per-plugin ConfigureServices - so a plugin can still override a
                // shared registration (the resolver is last-registration-wins).
                AssemblyServiceRegistrars.Resolve(GetType())?.Register(localContext.Resolver, localContext);
                ConfigureServices(localContext.Resolver, localContext);
                // Then the test harness's replacements, when it registered any (Dataverse never does): a test's
                // WithService(...) wins over both the registrar and ConfigureServices.
                (serviceProvider.GetService(typeof(IServiceOverrides)) as IServiceOverrides)?.Apply(localContext.Resolver);

                localContext.Trace(
                    "Executing {0}. Message={1}, Stage={2}, Mode={3}, Depth={4}",
                    GetType().FullName,
                    localContext.MessageName,
                    localContext.Stage,
                    localContext.Mode,
                    localContext.Depth);
                localContext.TraceExecutionSnapshot("start", snapshotDetail);

                EnforceDepth(localContext);
                if (!DispatchesToHandler)
                {
                    ReportHandlerInvoked(localContext);
                }

                ExecuteDataversePlugin(localContext);
                localContext.Trace("Execution completed in {0} ms.", stopwatch.ElapsedMilliseconds);
                // A metadata snapshot would only repeat the start one; a full one shows the values the handler set.
                if (snapshotDetail == TraceSnapshotDetail.Full)
                {
                    localContext.TraceExecutionSnapshot("success", snapshotDetail);
                }
            }
            // The diagnostics in the failure handlers are best-effort: a tracing service that fails too must never
            // replace the original failure (or a business exception's message) with its own exception.
            catch (InvalidPluginExecutionException)
            {
                TryDiagnose(() => localContext.TraceExecutionSnapshot("invalid-plugin-exception", snapshotDetail));
                throw;
            }
            // A failed organization service call (duplicate key, missing record, missing privilege, throttling...) is
            // rethrown unchanged. Dataverse passes a plug-in's FaultException<OrganizationServiceFault> through to the
            // caller with its own ErrorCode (and the Web API maps it to the matching HTTP status), whereas any
            // InvalidPluginExecutionException reaches the caller as the generic 0x80040265 - even the constructor that
            // takes an error code only adds it as a SubErrorCode detail. Verified live on Dataverse 9.2.
            catch (FaultException<OrganizationServiceFault> fault)
            {
                TryDiagnose(() => localContext.Trace("Organization service fault: {0}", fault));
                TryDiagnose(() => localContext.TraceExecutionSnapshot("organization-service-fault", snapshotDetail));
                throw;
            }
            catch (PluginBusinessException businessException)
            {
                // ToString, not Message: the business exception never reaches the caller, so its stack trace (and any
                // inner exception) is recorded here or nowhere.
                TryDiagnose(() => localContext.Trace("Business exception: {0}", businessException));
                TryDiagnose(() => localContext.TraceExecutionSnapshot("business-exception", snapshotDetail));
                if (businessException.ErrorCode is int errorCode)
                {
                    throw new InvalidPluginExecutionException(OperationStatus.Failed, errorCode, businessException.Message);
                }

                throw new InvalidPluginExecutionException(businessException.Message);
            }
            catch (Exception ex)
            {
                TryDiagnose(() => localContext.Trace("Unhandled exception: {0}", ex));
                TryDiagnose(() => localContext.TraceExecutionSnapshot("unhandled-exception", snapshotDetail));
                // The caller sees only this message, so by default it names no exception type and repeats no raw
                // message (either can carry record values, endpoints or query fragments): it gives the correlation id
                // that finds the full detail in the plug-in trace log (the "Unhandled exception" line above). The
                // original exception stays the inner exception, for the test harness.
                throw new InvalidPluginExecutionException(UnexpectedErrorMessage(localContext, ex), ex);
            }
            finally
            {
                // Dispose the transient IDisposable services the resolver created for this execution (scoped and
                // platform-owned services are left untouched). Runs on both the success and failure paths.
                localContext.Resolver.Dispose();
            }
        }

        /// <summary>
        /// How much the automatic execution snapshots (at start, and on failure) write to the plug-in trace log.
        /// Defaults to <see cref="TraceSnapshotDetail.Metadata"/> (names, ids and counts, no record values), so
        /// personal data and secrets never land in the trace log by default. Override with
        /// <see cref="TraceSnapshotDetail.Full"/> while debugging to also trace every value, at start and on success.
        /// </summary>
        /// <remarks>
        /// <see cref="TraceSnapshotDetail.None"/> drops the snapshots entirely (failures included), leaving the plug-in
        /// trace log budget (about 10 KB per execution) to the plugin's own traces.
        /// </remarks>
        protected virtual TraceSnapshotDetail AutoSnapshotDetail => TraceSnapshotDetail.Metadata;

        /// <summary>
        /// Whether the wrapped organization services (<see cref="ILocalPluginContext.Service"/>,
        /// <see cref="ILocalPluginContext.SystemService"/>, <see cref="ILocalPluginContext.AsUser"/>) trace every
        /// Dataverse call: a start line and an end line with the duration. On by default; override to return
        /// <c>false</c> for a plugin that makes many calls, whose two lines each would otherwise crowd its own traces
        /// out of the plug-in trace log (capped at about 10 KB per execution). A failed call is still traced, on one
        /// line naming the operation, the table or request, and the exception type. Like the other <c>AutoTrace</c>
        /// settings it applies to this plugin class only; override it on a shared base class to cover a whole project.
        /// </summary>
        protected virtual bool AutoTraceServiceCalls => true;

        /// <summary>
        /// Builds the message the caller sees when the plug-in fails with an unexpected exception (anything other than
        /// an <see cref="InvalidPluginExecutionException"/>, a <see cref="PluginBusinessException"/> or an organization
        /// service fault). The default is generic and gives the correlation id, so it exposes no exception type or raw
        /// message. The full exception is traced, best-effort: the plug-in trace log keeps it unless the environment's
        /// Plugin Trace Log setting is <c>Off</c>. Override to return a more detailed message, for example while
        /// developing.
        /// </summary>
        /// <param name="context">The execution context.</param>
        /// <param name="exception">The unexpected exception.</param>
        /// <returns>The caller-facing message.</returns>
        protected virtual string FormatUnexpectedErrorMessage(ILocalPluginContext context, Exception exception) =>
            DefaultUnexpectedErrorMessage(context);

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
        /// Sandbox-safe retry wrapper for organization service operations: retries only safe-to-replay throttling
        /// faults (timeouts are not retried, a timed-out write may already have committed), except inside a database
        /// transaction (a synchronous pre/post-operation step), where a failed call aborts the transaction and the
        /// operation therefore runs once. See <see cref="RetryPolicy"/> for the idempotency contract.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="context">The plugin context.</param>
        /// <param name="operation">Operation to execute.</param>
        /// <returns>Operation result.</returns>
        protected static T ExecuteWithRetry<T>(ILocalPluginContext context, Func<T> operation)
        {
            return RetryPolicy.Execute(context, operation);
        }

        /// <summary>
        /// Transaction-aware retry wrapper that also retries timeouts. Use only for an idempotent operation (a read, or
        /// a write that is safe to replay); a non-idempotent write may already have committed before a timeout surfaced.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="context">The plugin context.</param>
        /// <param name="operation">Idempotent operation to execute.</param>
        /// <returns>Operation result.</returns>
        protected static T ExecuteIdempotentWithRetry<T>(ILocalPluginContext context, Func<T> operation)
        {
            return RetryPolicy.Execute(context, operation, retryOnTimeout: true);
        }

        /// <summary>
        /// Executes plugin business logic.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected abstract void ExecuteDataversePlugin(ILocalPluginContext ctx);

        // Runs one diagnostic step of a failure handler; any exception it throws is swallowed so the failure being
        // handled is the one that surfaces.
        private static void TryDiagnose(Action diagnostic)
        {
            try
            {
                diagnostic();
            }
            catch (Exception)
            {
                // Ignored: diagnostics must never mask the original failure.
            }
        }

        // The caller-facing message for an unexpected exception. An override that throws or returns nothing falls back
        // to the default message, so the hook can never replace the original failure.
        private string UnexpectedErrorMessage(ILocalPluginContext context, Exception exception)
        {
            try
            {
                string message = FormatUnexpectedErrorMessage(context, exception);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch (Exception formatException)
            {
                TryDiagnose(() => context.Trace("FormatUnexpectedErrorMessage failed: {0}", formatException));
            }

            return DefaultUnexpectedErrorMessage(context);
        }

        private static string DefaultUnexpectedErrorMessage(ILocalPluginContext context) =>
            $"An unexpected error occurred. Correlation id: {context.CorrelationId}.";

        // Best-effort trace of a failure that happened before the context existed. Tracing must never mask the
        // original failure, so any error while tracing is swallowed.
        private void TryTraceStartupFailure(IServiceProvider serviceProvider, Exception exception)
        {
            try
            {
                (serviceProvider.GetService(typeof(ITracingService)) as ITracingService)?.Trace(
                    "Failed to build the execution context for {0}: {1}", GetType().FullName, exception.ToString());
            }
            catch (Exception)
            {
                // Ignored: the original exception is what the caller needs to see.
            }
        }

        /// <summary>
        /// Whether the step this execution belongs to sets its own depth limit (<c>.SkipWhenDepthAbove(n)</c>), which
        /// then replaces the plugin-level guard for it.
        /// </summary>
        private protected virtual bool HasStepDepthLimit(ILocalPluginContext context) => false;

        /// <summary>
        /// Every runtime path that decides not to call the handler goes through here: it traces why and reports it to
        /// the Vela.Testing pipeline (if one runs the plug-in), whose execution log then shows the step as invoked
        /// with its handler not run.
        /// </summary>
        private protected static void SkipHandler(ILocalPluginContext context, string reason)
        {
            context.Trace("{0}", reason);
            (context as LocalPluginContext)?.ReportHandlerSkipped(reason);
        }

        /// <summary>
        /// Whether <see cref="ExecuteDataversePlugin"/> is Vela's own dispatch, which reports the handler call itself
        /// (<see cref="EnterHandler"/>). When it is not (the plug-in overrides it), the override is the handler, and
        /// <see cref="Execute"/> reports the call just before it.
        /// </summary>
        private protected virtual bool DispatchesToHandler => false;

        // The handler-call boundary of Vela's dispatch: the "Method" telemetry dimension, the report to the test
        // harness, and the handler-entry trace.
        private protected void EnterHandler(ILocalPluginContext context, string? handlerName)
        {
            (context as LocalPluginContext)?.SetCurrentHandler(handlerName);
            ReportHandlerInvoked(context);
            TraceHandlerEntry(context, handlerName);
        }

        private protected static void ReportHandlerInvoked(ILocalPluginContext context)
            => (context as LocalPluginContext)?.ReportHandlerInvoked();

        private void EnforceDepth(ILocalPluginContext context)
        {
            if (context.Depth <= MaxDepth || HasStepDepthLimit(context))
            {
                return;
            }

            SkipHandler(context, $"Depth guard triggered. Depth={context.Depth}, MaxDepth={MaxDepth}");
            throw new InvalidPluginExecutionException("Plugin execution stopped to prevent recursion.");
        }
    }
}
