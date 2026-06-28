using System.Collections.Generic;
using System.Linq;
using System.ComponentModel.DataAnnotations;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vela.Plugin
{
    /// <summary>
    /// Generic plugin base that deserializes and validates config JSON.
    /// </summary>
    /// <typeparam name="TConfig">Configuration POCO type.</typeparam>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PluginBase{TConfig}"/> class.
    /// </remarks>
    /// <param name="unsecureConfig">Unsecure config JSON.</param>
    /// <param name="secureConfig">Secure config JSON.</param>
    /// <param name="maxDepth">Depth guard threshold.</param>
    public abstract class PluginBase<TConfig>(string unsecureConfig, string secureConfig, int maxDepth = 8) : PluginBase(unsecureConfig, secureConfig, maxDepth) where TConfig : class, new()
    {
        /// <summary>
        /// Gets validated plugin configuration.
        /// </summary>
        protected TConfig Configuration { get; } = ParseAndValidate(unsecureConfig, secureConfig);

        private static TConfig ParseAndValidate(string unsecure, string secure)
        {
            TConfig configuration;
            try
            {
                JObject merged = new JObject();
                if (!string.IsNullOrWhiteSpace(unsecure))
                {
                    merged.Merge(JObject.Parse(unsecure), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                }

                if (!string.IsNullOrWhiteSpace(secure))
                {
                    merged.Merge(JObject.Parse(secure), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                }

                configuration = merged.ToObject<TConfig>() ?? throw new InvalidPluginExecutionException("Plugin configuration JSON is invalid.");
            }
            catch (JsonException ex)
            {
                // Sandbox contract: surface every failure as InvalidPluginExecutionException, never a raw
                // Newtonsoft exception (which the platform would log as an unhandled framework error).
                throw new InvalidPluginExecutionException("Plugin configuration JSON is invalid: " + ex.Message);
            }

            ValidationContext validationContext = new ValidationContext(configuration, serviceProvider: null, items: null);
            List<ValidationResult> validationResults = new List<ValidationResult>();
            bool isValid = Validator.TryValidateObject(configuration, validationContext, validationResults, validateAllProperties: true);
            if (!isValid)
            {
                string message = string.Join("; ", validationResults.Select(static result => result.ErrorMessage));
                throw new InvalidPluginExecutionException("Plugin configuration validation failed: " + message);
            }

            return configuration;
        }
    }
}
