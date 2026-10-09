using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// PackageScanner rejects, before any write, a step image Dataverse would refuse for the step's message and effective
// stage, so plan fails with a clear error instead of apply failing mid-run (e.g. 0x8004416E for a post-image before
// PostOperation). Bulk images are valid and pass through.
public sealed class PackageScannerImageGuardTests
{
    private static JsonElement Step(string message, int stage, bool pre = false, bool post = false)
    {
        string preArr = pre ? "[{\"alias\":\"pre\"}]" : "[]";
        string postArr = post ? "[{\"alias\":\"post\"}]" : "[]";
        return JsonDocument.Parse(
            $"{{\"message\":\"{message}\",\"stage\":{stage},\"preImages\":{preArr},\"postImages\":{postArr}}}").RootElement;
    }

    [Theory]
    [InlineData("CreateMultiple", 40, false, true)]  // Ids
    [InlineData("UpdateMultiple", 10, true, false)]  // Targets, PreValidation
    [InlineData("UpdateMultiple", 20, true, false)]
    [InlineData("UpdateMultiple", 40, true, true)]
    [InlineData("Update", 10, true, false)]
    [InlineData("Delete", 10, true, false)]
    [InlineData("Create", 40, false, true)]
    [InlineData("Create", 10, false, false)]         // no image at all
    [InlineData("RouteTo", 40, false, false)]
    public void Accepts_valid_images(string message, int stage, bool pre, bool post)
        => Assert.Null(PackageScanner.GetInvalidImageError(Step(message, stage, pre, post)));

    [Theory]
    [InlineData("Update", 10, false, true, "post-image 'post'")]          // 0x8004416E
    [InlineData("UpdateMultiple", 10, false, true, "post-image 'post'")]
    [InlineData("Update", 20, false, true, "post-image 'post'")]
    [InlineData("Create", 10, true, false, "pre-image 'pre'")]            // 0x8004416D
    [InlineData("CreateMultiple", 40, true, false, "pre-image 'pre'")]
    [InlineData("Delete", 40, false, true, "post-image 'post'")]
    [InlineData("RouteTo", 40, true, false, "pre-image 'pre'")]           // 0x80044189
    public void Rejects_images_dataverse_refuses(string message, int stage, bool pre, bool post, string what)
    {
        string? error = PackageScanner.GetInvalidImageError(Step(message, stage, pre, post));

        Assert.NotNull(error);
        Assert.Contains(what, error);
        Assert.Contains($"'{message}'", error);
    }

    [Fact]
    public void Names_the_stage_and_the_reason()
    {
        string? error = PackageScanner.GetInvalidImageError(Step("Update", 10, post: true));

        Assert.Contains("PreValidation", error);
        Assert.Contains("a post-image only exists at PostOperation", error);
    }
}
