using System;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Vela.StepBuilder;

namespace Vela.Testing
{
    /// <summary>
    /// A discovered (plugin type, step) pair plus a cached constructor used to create a fresh plugin
    /// instance for each execution (mirroring the platform, which instantiates per invocation).
    /// </summary>
    internal sealed class PipelineRegistration
    {
        private readonly ConstructorInfo _constructor;

        public PipelineRegistration(Type pluginType, StepDefinition step)
        {
            PluginType = pluginType;
            Step = step;
            _constructor = pluginType.GetConstructor(new[] { typeof(string), typeof(string) })
                ?? throw new InvalidOperationException(
                    $"Plugin '{pluginType.FullName}' must have a (string unsecureConfig, string secureConfig) constructor to be simulated.");
        }

        public Type PluginType { get; }

        public StepDefinition Step { get; }

        public IPlugin CreateInstance()
        {
            return (IPlugin)_constructor.Invoke(new object?[] { null, null });
        }
    }
}
