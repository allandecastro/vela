namespace BrokenPluginFixture
{
    // Name starts with "EntityPluginBase", so PackageScanner.IsPluginType identifies the derived type as a plugin.
    public abstract class EntityPluginBaseBroken
    {
        protected EntityPluginBaseBroken(string unsecureConfig, string secureConfig)
        {
        }
    }

    // A discovered plugin that cannot be introspected: it has no public (string, string) constructor, which
    // PackageScanner requires to instantiate the type. BuildLocalStateFromPackages therefore raises
    // BuildIntrospectionFailure ("Missing (string, string) constructor.").
    public sealed class BrokenStepPlugin : EntityPluginBaseBroken
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
