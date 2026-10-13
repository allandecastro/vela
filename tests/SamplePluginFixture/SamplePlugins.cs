namespace SamplePluginFixture
{
    // Base types whose NAMES start with "EntityPluginBase" / "CustomApiBase". PackageScanner.IsPluginType walks the
    // base chain and matches by name prefix, so a concrete type below is discovered without deriving from the real
    // Vela.Plugins base classes (which would drag in Microsoft.Xrm.Sdk). Abstract, so they are skipped themselves.
    public abstract class EntityPluginBase
    {
        protected EntityPluginBase(string unsecureConfig, string secureConfig)
        {
        }
    }

    public abstract class CustomApiBase
    {
        protected CustomApiBase(string unsecureConfig, string secureConfig)
        {
        }
    }

    // A concrete plugin: a public (string, string) constructor and a public GetStepDefinitions() returning a
    // non-empty array. The returned objects serialize (camelCase) to the shape DefinitionNormalizer reads.
    public sealed class SampleAccountPlugin : EntityPluginBase
    {
        public SampleAccountPlugin(string unsecureConfig, string secureConfig)
            : base(unsecureConfig, secureConfig)
        {
        }

        public object[] GetStepDefinitions()
        {
            return new object[]
            {
                new
                {
                    PluginTypeName = "SamplePluginFixture.SampleAccountPlugin",
                    Message = "Create",
                    EntityLogicalName = "account",
                    Mode = 0,   // synchronous
                    Stage = 20, // PreOperation
                    ExecutionOrder = 1,
                    Enabled = true,
                    StepKey = "main",
                    SecureConfigName = "SampleApiKey",
                },
            };
        }
    }

    // A concrete Custom API: same constructor shape, a public GetCustomApiDefinition() returning one object.
    public sealed class SampleCustomApi : CustomApiBase
    {
        public SampleCustomApi(string unsecureConfig, string secureConfig)
            : base(unsecureConfig, secureConfig)
        {
        }

        public object GetCustomApiDefinition()
        {
            return new
            {
                PluginTypeName = "SamplePluginFixture.SampleCustomApi",
                UniqueName = "sample_DoThing",
                // Typed: the framework's CustomApiBase<TRequest, TResponse> sets the request type name; a lightweight API leaves it empty.
                RequestTypeName = "SamplePluginFixture.DoThingRequest",
                IsFunction = false,
                IsBound = false,
            };
        }
    }
}

namespace Microsoft.Xrm.Sdk
{
    // Look-alike of the SDK's IPlugin (matched by full name by PackageScanner.IsUnmanagedPluginType).
    public interface IPlugin
    {
        void Execute(System.IServiceProvider serviceProvider);
    }
}

namespace SamplePluginFixture
{
    // A classic plugin living in the same assembly as the Vela plugins (registered with another tool): the scan must
    // report it as unmanaged so apply leaves its registrations alone.
    public sealed class LegacyPlugin : Microsoft.Xrm.Sdk.IPlugin
    {
        public void Execute(System.IServiceProvider serviceProvider)
        {
        }
    }
}
