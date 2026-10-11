using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vela.Context;
using Vela.Constants;
using Vela.Steps;
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
    /// (<c>service.Execute(new XxxRequest { ... })</c>), and reacting to it (<c>On&lt;XxxRequest&gt;()</c>).
    /// </para>
    /// Initializes a new instance of the <see cref="CustomApiBase{TRequest, TResponse}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class CustomApiBase<TRequest, TResponse>(string unsecureConfig, string secureConfig, int maxDepth = Vela.Plugin.PluginBase.DefaultMaxDepth) : CustomApiBase(unsecureConfig, secureConfig, maxDepth)
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
        /// Builds the typed Custom API definition (input and output parameters). Runs under the initialization
        /// lock held by <see cref="CustomApiBase.GetCustomApiDefinition"/> and is memoized, so the shared
        /// <see cref="_activeBuilder"/> is mutated safely without a race on the reused sandbox instance.
        /// </summary>
        /// <returns>The Custom API definition.</returns>
        protected sealed override CustomApiDefinition BuildDefinitionCore()
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
            definition.PluginStep = definition.CreatePluginStep();
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
            ParameterCollection inputParameters = ctx.PluginExecutionContext.InputParameters;
            var request = new TRequest { Parameters = inputParameters };
            VerifyRequestBindsToParameters(request, inputParameters);
            return request;
        }

        /// <summary>
        /// Guard-rail: the request is hydrated by aliasing its <c>Parameters</c> bag onto the live
        /// <c>InputParameters</c>, so a typed property must read through that bag (e.g. <c>get =&gt; Get&lt;T&gt;();</c>,
        /// as generated types and <see cref="Vela.Messages.TypedRequest"/> do). A plain auto-property has its own
        /// backing field that nothing populates, so an input value would silently bind to its default. Detect that
        /// precisely - an input key matching an auto-property - and fail loudly instead of returning wrong data.
        /// </summary>
        private static void VerifyRequestBindsToParameters(TRequest request, ParameterCollection inputParameters)
        {
            // The auto-properties of a request type never change, so they are found once per type (the cache below);
            // a well-formed request has none and the per-execution check is a single emptiness test.
            Type type = request.GetType();
            Dictionary<string, PropertyInfo> autoProperties = type == typeof(TRequest) ? RequestAutoProperties : FindAutoProperties(type);
            if (autoProperties.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, object> input in inputParameters)
            {
                if (autoProperties.TryGetValue(input.Key, out PropertyInfo? property))
                {
                    throw new InvalidPluginExecutionException(
                        $"Custom API request '{type.FullName}' declares '{property.Name}' as a plain auto-property, so the " +
                        $"input parameter '{input.Key}' would silently bind to its default. Back the property with the " +
                        $"Parameters bag instead (e.g. 'get => Get<{property.PropertyType.Name}>(); set => Set(value);') " +
                        "or derive the request from Vela.Messages.TypedRequest, then regenerate with the current CLI.");
                }
            }
        }

        // The public instance properties of TRequest whose getter is compiler-generated (plain auto-properties),
        // keyed by name exactly as Type.GetProperty matches them. Computed once per closed generic type.
        private static readonly Dictionary<string, PropertyInfo> RequestAutoProperties = FindAutoProperties(typeof(TRequest));

        private static Dictionary<string, PropertyInfo> FindAutoProperties(Type type)
        {
            Dictionary<string, PropertyInfo> autoProperties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                MethodInfo? getter = property.GetGetMethod();
                if (getter != null && getter.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                    && !autoProperties.ContainsKey(property.Name))
                {
                    autoProperties[property.Name] = property;
                }
            }

            return autoProperties;
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
