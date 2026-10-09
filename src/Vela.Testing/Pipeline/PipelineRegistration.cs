using System;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Vela.Steps;

namespace Vela.Testing
{
    /// <summary>
    /// A discovered (plugin type, step) pair and the plugin instance that runs it. Like the platform, which caches a
    /// plugin instance per step registration and reuses it for every execution (so a plugin must be stateless), the
    /// harness constructs the plugin once per step - with the step's configuration - and reuses it, so a plugin that
    /// keeps state in fields between executions misbehaves in tests as it would in production.
    /// </summary>
    internal sealed class PipelineRegistration
    {
        private readonly ConstructorInfo _constructor;
        private IPlugin? _instance;
        private string? _instanceSecureConfig;

        public PipelineRegistration(Type pluginType, StepDefinition step)
        {
            PluginType = pluginType;
            Step = step;
            _constructor = PluginActivator.RequireConfigurationConstructor(pluginType, "to be simulated");
        }

        public Type PluginType { get; }

        public StepDefinition Step { get; }

        // The plugin instance for this step, constructed the way the platform does: the step's unsecure configuration
        // and the secure configuration value (resolved by the pipeline from the step's SecureConfigName) reach the
        // (unsecureConfig, secureConfig) constructor; an unset configuration is passed as null. The instance is reused
        // until the secure configuration value changes (a re-registration on the platform).
        public IPlugin GetInstance(string? secureConfig)
        {
            string? secure = string.IsNullOrEmpty(secureConfig) ? null : secureConfig;
            if (_instance == null || !string.Equals(_instanceSecureConfig, secure, StringComparison.Ordinal))
            {
                string? unsecureConfig = string.IsNullOrEmpty(Step.UnsecureConfig) ? null : Step.UnsecureConfig;
                _instance = PluginActivator.Create(_constructor, unsecureConfig, secure);
                _instanceSecureConfig = secure;
            }

            return _instance;
        }
    }
}
