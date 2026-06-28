using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class ApplyResumeReportTests
{
    [Fact]
    public void Report_lists_completed_and_remaining_operations()
    {
        var report = ApplyResumeReport.Build(
            failedOperation: "create custom-api Zava.MyApi",
            failureMessage: "boom",
            completed: ["delete step S1", "publish plugin package(s) and resolve plugin types"],
            remaining: ["create step Zava.AccountPlugin"]);

        string text = string.Join("\n", report);

        Assert.Contains("apply failed mid-run", text);
        Assert.Contains("failed at: create custom-api Zava.MyApi", text);
        Assert.Contains("reason: boom", text);
        Assert.Contains("completed (2):", text);
        Assert.Contains("    + delete step S1", text);
        Assert.Contains("not applied (1):", text);
        Assert.Contains("    - create step Zava.AccountPlugin", text);
        Assert.Contains("re-run 'apply --execute' to resume", text);
    }

    [Fact]
    public void Report_handles_empty_completed_and_remaining()
    {
        var report = ApplyResumeReport.Build("delete step S1", "boom", [], []);
        string text = string.Join("\n", report);

        Assert.Contains("completed (0):", text);
        Assert.Contains("not applied (0):", text);
        Assert.Equal(2, report.Count(l => l.Trim() == "(none)"));
    }
}
