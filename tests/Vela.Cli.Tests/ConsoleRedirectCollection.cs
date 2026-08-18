using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Groups tests that redirect the process-global <see cref="System.Console.Error"/> (to capture the apply
/// resume report). They must not run concurrently with each other or any other test, or one test's
/// <c>Console.SetError</c> would clobber another's capture. <c>DisableParallelization</c> makes this collection
/// run on its own.
/// </summary>
[CollectionDefinition("ConsoleRedirect", DisableParallelization = true)]
public sealed class ConsoleRedirectCollection
{
}
