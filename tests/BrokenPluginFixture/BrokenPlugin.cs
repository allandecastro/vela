namespace BrokenPluginFixture
{
    // Simple name is exactly "EntityPluginBase" (mimicking the real Vela base), so PackageScanner.IsPluginType
    // identifies the derived type as a plugin. A suffixed name (e.g. EntityPluginBaseHelper) would no longer match.
    public abstract class EntityPluginBase
    {
        protected EntityPluginBase(string unsecureConfig, string secureConfig)
        {
        }
    }

    // A discovered plugin that cannot be introspected: it has no public (string, string) constructor, which
    // PackageScanner requires to instantiate the type. BuildLocalStateFromPackages therefore raises
    // BuildIntrospectionFailure ("Missing (string, string) constructor.").
    public sealed class BrokenStepPlugin : EntityPluginBase
    {
        public BrokenStepPlugin()
            : base(null, null)
        {
        }

        public object[] GetStepDefinitions()
        {
            return new object[] { new { PluginTypeName = "BrokenPluginFixture.BrokenStepPlugin", Message = "Create" } };
        }
    }
}
