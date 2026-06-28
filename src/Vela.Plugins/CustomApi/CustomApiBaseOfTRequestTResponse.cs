using System;
using System.Reflection;
using Vela.Context;
using Vela.Constants;
using Vela.StepBuilder;
using Newtonsoft.Json;

namespace Vela.CustomApi
{
    /// <summary>
    /// Base class for Custom API plugins with strongly-typed request and response objects.
    /// </summary>
    /// <typeparam name="TRequest">The request parameter type.</typeparam>
    /// <typeparam name="TResponse">The response parameter type.</typeparam>
    /// <remarks>
    /// Initializes a new instance of the <see cref="CustomApiBase{TRequest, TResponse}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure JSON configuration.</param>
    /// <param name="secureConfig">Secure JSON configuration.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class CustomApiBase<TRequest, TResponse>(string unsecureConfig, string secureConfig, int maxDepth = 8) : CustomApiBase(unsecureConfig, secureConfig, maxDepth)
        where TRequest : class, new()
        where TResponse : class, new()
    {
        // Reflected once per closed generic type (static initialization is thread-safe), rather than on
        // every execution. Public instance properties only, which the plugin sandbox permits.
        private static readonly PropertyInfo[] RequestProperties =
            typeof(TRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        private static readonly PropertyInfo[] ResponseProperties =
            typeof(TResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance);

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
        /// Maps input parameters to the request object.
        /// Override to customize mapping behavior.
        /// </summary>
        /// <param name="ctx">The local plugin context.</param>
        /// <returns>The populated request object.</returns>
        protected virtual TRequest MapInputToRequest(ILocalPluginContext ctx)
        {
            var request = new TRequest();
            var inputParams = ctx.PluginExecutionContext.InputParameters;

            foreach (var property in RequestProperties)
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                var paramName = property.Name;
                if (inputParams.Contains(paramName))
                {
                    var value = inputParams[paramName];
                    var convertedValue = ConvertValue(value, property.PropertyType);
                    property.SetValue(request, convertedValue);
                }
            }

            return request;
        }

        /// <summary>
        /// Maps the response object to output parameters.
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

            var outputParams = ctx.PluginExecutionContext.OutputParameters;

            foreach (var property in ResponseProperties)
            {
                if (!property.CanRead)
                {
                    continue;
                }

                var paramName = property.Name;
                var value = property.GetValue(response);
                if (value != null)
                {
                    outputParams[paramName] = value;
                }
            }
        }

        /// <summary>
        /// Converts a value to the target type.
        /// Override to add custom type conversions.
        /// </summary>
        /// <param name="value">The value to convert.</param>
        /// <param name="targetType">The target type.</param>
        /// <returns>The converted value.</returns>
        protected virtual object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
            {
                return null;
            }

            var valueType = value.GetType();
            if (targetType.IsAssignableFrom(valueType))
            {
                return value;
            }

            // Handle JSON strings to complex objects
            if (value is string jsonString && targetType.IsClass && targetType != typeof(string))
            {
                return JsonConvert.DeserializeObject(jsonString, targetType);
            }

            // Handle simple type conversions
            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, value);
            }

            return Convert.ChangeType(value, targetType);
        }
    }
}
