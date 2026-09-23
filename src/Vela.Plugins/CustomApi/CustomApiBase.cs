using System;
using System.Reflection;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi
{
    /// <summary>
    /// Base class for Custom API plugins.
    /// Custom APIs are invoked via their registered unique name.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="CustomApiBase"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class CustomApiBase(string unsecureConfig, string secureConfig, int maxDepth = 8) : PluginBase(unsecureConfig, secureConfig, maxDepth)
    {
        private volatile CustomApiDefinition? _customApiDefinition;
        private ICustomApiBuilder? _activeBuilder;
        private readonly object _initLock = new object();

        /// <summary>
        /// Gets the step definition for this plugin (used for registration manifest generation).
        /// </summary>
        public StepDefinition GetStepDefinition()
        {
            return GetCustomApiDefinition().PluginStep;
        }

        /// <summary>
        /// Gets the Custom API metadata definition for registration and synchronization.
        /// </summary>
        public virtual CustomApiDefinition GetCustomApiDefinition()
        {
            CustomApiDefinition? snapshot = _customApiDefinition;
            if (snapshot != null)
            {
                return snapshot;
            }

            // The Dataverse sandbox caches and concurrently reuses a single plugin instance, so the one-time
            // build below — which mutates the shared _activeBuilder field — must be serialized. Otherwise two
            // concurrent first executions race: one can null _activeBuilder while the other is still inside
            // RegisterCustomApi(...), throwing spuriously or cross-wiring the two builders. This mirrors the
            // _stepInitLock guard on EntityPluginBase.GetStepDefinitions().
            lock (_initLock)
            {
                return _customApiDefinition ??= BuildDefinition();
            }
        }

        /// <summary>
        /// Configures Custom API metadata using the direct registration style.
        /// </summary>
        protected virtual void RegisterCustomApi()
        {
        }

        /// <summary>
        /// Starts direct lightweight Custom API configuration with unique name and handler.
        /// </summary>
        /// <param name="uniqueName">Custom API unique name.</param>
        /// <param name="handlerMethodName">Handler method name.</param>
        /// <returns>The active Custom API builder.</returns>
        protected ICustomApiBuilder RegisterCustomApi(string uniqueName, string handlerMethodName)
        {
            ICustomApiBuilder builder = GetActiveBuilder();
            return builder.UniqueName(uniqueName).Handler(handlerMethodName);
        }

        /// <summary>
        /// Starts direct lightweight Custom API configuration with unique name and handler delegate.
        /// </summary>
        /// <param name="uniqueName">Custom API unique name.</param>
        /// <param name="handler">Handler delegate.</param>
        /// <returns>The active Custom API builder.</returns>
        protected ICustomApiBuilder RegisterCustomApi(string uniqueName, Action<ILocalPluginContext> handler)
        {
            ICustomApiBuilder builder = GetActiveBuilder();
            return builder.UniqueName(uniqueName).Handler(handler);
        }

        /// <summary>
        /// Executes the handler configured via the lightweight Custom API fluent builder.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        protected void ExecuteRegisteredHandler(ILocalPluginContext ctx)
        {
            if (ctx == null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }

            string methodName = GetCustomApiDefinition().HandlerMethodName;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new InvalidPluginExecutionException(
                    $"Custom API '{GetType().FullName}' must configure Handler(...) or override ExecuteDataversePlugin.");
            }

            (ctx as LocalPluginContext)?.SetCurrentHandler(methodName);
            HandlerInvoker.ResolveAndInvoke(
                this,
                methodName,
                IsSupportedHandlerSignature,
                [ctx],
                subjectLabel: $"Custom API '{GetType().FullName}'",
                handlerNoun: "handler",
                expectedSignature: $"void {methodName}(ILocalPluginContext)");
        }

        /// <summary>
        /// Executes the lightweight Custom API by dispatching to the registered handler.
        /// Override only when you need fully custom execution behavior.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            // A Custom API's request parameters and response properties carry bare-identifier uniquenames equal to
            // their plain names, so Dataverse delivers inputs under the plain name (which the handler reads via
            // ctx.GetInputParameter("Name")) and reads outputs under the plain name (which the handler writes via
            // ctx.SetOutputParameter("Name", value)). No name bridging is needed - the plain name is the contract
            // end to end.
            ExecuteRegisteredHandler(ctx);
        }

        private CustomApiDefinition BuildDefinition()
        {
            ICustomApiBuilder builder = Vela.Register.RegisterCustomApi();
            _activeBuilder = builder;
            try
            {
                RegisterCustomApi();
            }
            finally
            {
                _activeBuilder = null;
            }

            CustomApiDefinition definition = builder.Build();
            definition.PluginTypeName = GetType().FullName ?? GetType().Name;
            definition.PluginStep = new StepDefinition
            {
                PluginTypeName = definition.PluginTypeName,
                Message = definition.UniqueName,
                Stage = PluginStages.MainOperation,
                Mode = ExecutionMode.Synchronous,
                HandlerMethodName = definition.HandlerMethodName
            };
            return definition;
        }

        private ICustomApiBuilder GetActiveBuilder()
        {
            if (_activeBuilder == null)
            {
                throw new InvalidOperationException(
                    $"Custom API '{GetType().FullName}' can only call RegisterCustomApi(uniqueName, handler) inside RegisterCustomApi().");
            }

            return _activeBuilder;
        }

        private static bool IsSupportedHandlerSignature(MethodInfo method)
        {
            return HandlerSignature.IsSupported(
                method,
                contextType => typeof(ILocalPluginContext).IsAssignableFrom(contextType));
        }
    }
}
