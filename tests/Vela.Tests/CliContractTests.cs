using System;
using System.Reflection;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Pins the reflection contract that the CLI (Vela.Cli/Cli/PackageScanner) relies on
    // when it scans consumer plugin assemblies. The CLI has NO compile-time reference to Core — it locates
    // these by name/shape via reflection — so a rename or signature change here would break `vela` silently
    // at runtime. These tests fail the build instead.
    public class CliContractTests
    {
        // Mirror of the constants hard-coded in PackageScanner.
        private const string PluginBaseName = "EntityPluginBase";
        private const string CustomApiBaseName = "CustomApiBase";
        private const string StepDefinitionsMethod = "GetStepDefinitions";
        private const string CustomApiDefinitionMethod = "GetCustomApiDefinition";

        private sealed class ContractSamplePlugin : EntityPluginBase<TestAccount>
        {
            public ContractSamplePlugin(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate()
                    .PostOperation()
                    .Sync()
                    .FilterOn(a => a.Name)
                    .Handle(OnUpdate);
            }

            private void OnUpdate(IUpdatePostContext<TestAccount> ctx) { }
        }

        private sealed class ContractSampleApi : CustomApiBase
        {
            public ContractSampleApi(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterCustomApi()
            {
                RegisterCustomApi("contract_SampleApi", ctx => { });
            }
        }

        [Fact]
        public void PluginType_satisfies_the_CLI_reflection_contract()
        {
            Type type = typeof(ContractSamplePlugin);

            Assert.True(HasBaseNamed(type, PluginBaseName), $"Plugin base type name must start with '{PluginBaseName}'.");

            ConstructorInfo? ctor = type.GetConstructor([typeof(string), typeof(string)]);
            Assert.NotNull(ctor);

            MethodInfo? method = type.GetMethod(StepDefinitionsMethod, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(method);

            object instance = ctor!.Invoke([null, null]);
            object? result = method!.Invoke(instance, null);

            Array steps = Assert.IsAssignableFrom<Array>(result);
            Assert.True(steps.Length > 0, "GetStepDefinitions() must return at least one step.");
            Assert.IsType<StepDefinition>(steps.GetValue(0));
        }

        [Fact]
        public void CustomApiType_satisfies_the_CLI_reflection_contract()
        {
            Type type = typeof(ContractSampleApi);

            Assert.True(HasBaseNamed(type, CustomApiBaseName), $"Custom API base type name must start with '{CustomApiBaseName}'.");

            ConstructorInfo? ctor = type.GetConstructor([typeof(string), typeof(string)]);
            Assert.NotNull(ctor);

            MethodInfo? method = type.GetMethod(CustomApiDefinitionMethod, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(method);

            object instance = ctor!.Invoke([null, null]);
            object? definition = method!.Invoke(instance, null);
            Assert.NotNull(definition);
        }

        // Same hierarchy walk PackageScanner.IsPluginType performs.
        private static bool HasBaseNamed(Type type, string baseTypeName)
        {
            for (Type? current = type.BaseType; current != null; current = current.BaseType)
            {
                if (current.Name.StartsWith(baseTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
