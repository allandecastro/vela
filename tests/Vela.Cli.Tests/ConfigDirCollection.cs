using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Groups tests that set the process-global <c>VELA_CONFIG_DIR</c> environment variable (UserConfig / logout).
/// The variable is process-wide, so two such tests running concurrently would point <c>UserConfig</c> at each
/// other's temp dir and race (a Set in one test lands in the other's directory). <c>DisableParallelization</c>
/// makes this collection run on its own so the env-var swap is always isolated.
/// </summary>
[CollectionDefinition("ConfigDir", DisableParallelization = true)]
public sealed class ConfigDirCollection
{
}
