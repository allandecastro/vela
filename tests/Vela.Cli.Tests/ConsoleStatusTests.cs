using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class ConsoleStatusTests
{
    [Fact]
    public void Run_WithProgress_InvokesActionAndReturnsResult_AndReportIsSafe()
    {
        // In the test host stderr is redirected, so no spinner is shown and the progress callback is a no-op;
        // it must still be safe to call, the action must run, and its result must flow back.
        bool ran = false;

        int result = ConsoleStatus.Run("Reading…", report =>
        {
            report("phase one");   // safe no-op when no spinner is drawn
            report("phase two");
            ran = true;
            return 42;
        });

        Assert.True(ran);
        Assert.Equal(42, result);
    }
}
