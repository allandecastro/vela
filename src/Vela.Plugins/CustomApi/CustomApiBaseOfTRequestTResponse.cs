using System;
using System.Collections.Generic;
using Vela.Context;
using Vela.Constants;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi
{
    /// <summary>
    /// Base class for Custom API plugins with strongly-typed request and response objects.
    /// </summary>
    /// <typeparam name="TRequest">The request type, derived from <see cref="OrganizationRequest"/>.</typeparam>
    /// <typeparam name="TResponse">The response type, derived from <see cref="OrganizationResponse"/>.</typeparam>
    /// <remarks>
    /// <para>
    /// The request and response are <see cref="OrganizationRequest"/>/<see cref="OrganizationResponse"/>-derived
    /// types whose typed properties read and write the underlying <c>Parameters</c>/<c>Results</c> bags. A single
    /// generated type therefore serves all three usages: authoring the Custom API (this base), calling it
    /// (<c>service.Execute(new XxxRequest { ... })</c>), and reacting to it (<c>OnMessage(...).Handler&lt;XxxRequest&gt;()</c>).
    /// </para>
    /// Initializes a new instance of the <see cref="CustomApiBase{TRequest, TResponse}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class CustomApiBase<TRequest, TResponse>(string unsecureConfig, string secureConfig, int maxDepth = 8) : CustomApiBase(unsecureConfig, secureConfig, maxDepth)
        where TRequest : OrganizationRequest, new()
        where TResponse : OrganizationResponse, new()
    {
        private ICustomApiBuilder<TRequest, TResponse>? _activeBuilder;

        /// <summary>
        /// Configures the Custom API metadata using the direct registration style.
        /// </summary>
        protected override void RegisterCustomApi()
        {
        }

        /// <summary>
        /// Starts direct typed Custom API configuration with a unique name.
        /// </summary>
        /// <param name="uniqueName">Custom API unique name.</param>
        /// <returns>The active Custom API builder.</returns>
        protected ICustomApiBuilder<TRequest, TResponse> RegisterCustomApi(string uniqueName)
        {
            if (_activeBuilder == null)
            {
                throw new InvalidOperationException(
                    $"Custom API '{GetType().FullName}' can only call RegisterCustomApi(uniqueName) inside RegisterCustomApi().");
            }

            return _activeBuilder.UniqueName(uniqueName);
        }

        /// <summary>
        /// Executes the Custom API business logic.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        /// <param name="request">The typed request parameters.</param>
        /// <returns>The typed response parameters.</returns>
        protected abstract TResponse Execute(ILocalPluginContext ctx, TRequest request);

        /// <summary>
        /// Executes the plugin by mapping input parameters to request, executing, and mapping response to output parameters.
        /// </summary>
        /// <param name="ctx">Local plugin context.</param>
        protected sealed override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            var request = MapInputToRequest(ctx);
            var response = Execute(ctx, request);
            MapResponseToOutput(ctx, response);
        }

        /// <summary>
        /// Gets the Custom API metadata definition including input and output parameters.
        /// </summary>
        /// <returns>The Custom API definition.</returns>
        public sealed override CustomApiDefinition GetCustomApiDefinition()
        {
            var builder = Vela.Register.RegisterCustomApi<TRequest, TResponse>();
            _activeBuilder = builder;
            try
            {
                RegisterCustomApi();
            }
            finally
            {
                _activeBuilder = null;
            }

            var definition = builder.Build();
            definition.PluginTypeName = GetType().FullName ?? GetType().Name;
            definition.PluginStep = new StepDefinition
            {
                PluginTypeName = definition.PluginTypeName,
                Message = definition.UniqueName,
                Stage = PluginStages.MainOperation,
                Mode = ExecutionMode.Synchronous
            };
            return definition;
        }

        /// <summary>
        /// Maps input parameters to the request object by hydrating a request over the live
        /// <see cref="Microsoft.Xrm.Sdk.IExecutionContext.InputParameters"/>, so the request's typed
        /// properties read the actual inputs.
        /// Override to customize mapping behavior.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        /// <returns>The populated request object.</returns>
        protected virtual TRequest MapInputToRequest(ILocalPluginContext ctx)
        {
            return new TRequest { Parameters = ctx.PluginExecutionContext.InputParameters };
        }

        /// <summary>
        /// Maps the response object to output parameters by copying its <c>Results</c> - written through the
        /// response's typed setters - into the context's
        /// <see cref="Microsoft.Xrm.Sdk.IExecutionContext.OutputParameters"/>.
        /// Override to customize mapping behavior.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        /// <param name="response">The response object to map.</param>
        protected virtual void MapResponseToOutput(ILocalPluginContext ctx, TResponse response)
        {
            if (response == null)
            {
                return;
            }

            var outputParameters = ctx.PluginExecutionContext.OutputParameters;
            foreach (KeyValuePair<string, object> result in response.Results)
            {
                outputParameters[result.Key] = result.Value;
            }
        }
    }
}
