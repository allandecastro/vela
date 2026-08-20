using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Dataverse supports no images on the CreateMultiple/UpdateMultiple messages, so PackageScanner rejects a bulk
// step that carries a pre/post image before apply. The in-memory harness allows such a registration (to simulate
// per-record bulk images), so the deploy-time rule lives here, not in the shared step builder.
public sealed class PackageScannerBulkImageGuardTests
{
    private static JsonElement Step(string message, bool pre = false, bool post = false)
    {
        string preArr = pre ? "[{\"alias\":\"pre\"}]" : "[]";
        string postArr = post ? "[{\"alias\":\"post\"}]" : "[]";
        return JsonDocument.Parse(
            $"{{\"message\":\"{message}\",\"preImages\":{preArr},\"postImages\":{postArr}}}").RootElement;
    }

    [Theory]
    [InlineData("CreateMultiple", false, true)]   // post-image on a bulk create
    [InlineData("UpdateMultiple", true, false)]   // pre-image on a bulk update
    [InlineData("UpdateMultiple", true, true)]
    public void Flags_bulk_step_with_images(string message, bool pre, bool post)
        => Assert.True(PackageScanner.IsBulkStepWithImages(Step(message, pre, post)));

    [Theory]
    [InlineData("CreateMultiple", false, false)]  // bulk, but no image -> fine
    [InlineData("UpdateMultiple", false, false)]
    [InlineData("Create", false, true)]           // images on the single messages are fine
    [InlineData("Update", true, true)]
    [InlineData("Delete", true, false)]
    public void Allows_non_bulk_or_imageless_steps(string message, bool pre, bool post)
        => Assert.False(PackageScanner.IsBulkStepWithImages(Step(message, pre, post)));
}
