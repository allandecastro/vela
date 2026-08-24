using System.Linq;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

/// <summary>
/// The web-resource branch of <see cref="DiffEngine"/>: identity is the name (path), a change is a differing
/// content hash, and it reconciles independently of steps / Custom APIs.
/// </summary>
public class DiffEngineWebResourceTests
{
    [Fact]
    public void Identity_Is_The_Web_Resource_Name()
    {
        Assert.Equal("web-resource:adc_/js/account/main.js", DiffEngine.GetIdentityKey(WebResource("adc_/js/account/main.js", "abc")));
    }

    [Fact]
    public void New_File_Is_Added()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(WebResource("adc_/js/a.js", "h1")),
            State());

        RegistrationItem added = Assert.Single(diff.Added);
        Assert.Equal("adc_/js/a.js", added.Type);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Missing_Local_File_Is_Removed()
    {
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(),
            State(WebResource("adc_/js/a.js", "h1")));

        RegistrationItem removed = Assert.Single(diff.Removed);
        Assert.Equal("adc_/js/a.js", removed.Type);
    }

    [Fact]
    public void Same_Name_Different_Hash_Is_Changed_Same_Hash_Is_Skipped()
    {
        RegistrationDiff changed = DiffEngine.BuildDiff(
            State(WebResource("adc_/js/a.js", "new")),
            State(WebResource("adc_/js/a.js", "old")));
        Assert.Single(changed.Changed);
        Assert.Empty(changed.Added);
        Assert.Empty(changed.Removed);

        RegistrationDiff idempotent = DiffEngine.BuildDiff(
            State(WebResource("adc_/js/a.js", "same")),
            State(WebResource("adc_/js/a.js", "same")));
        Assert.Empty(idempotent.Changed);
        Assert.Empty(idempotent.Added);
        Assert.Empty(idempotent.Removed);
    }

    [Fact]
    public void Web_Resources_Do_Not_Mix_With_Custom_Apis()
    {
        // A web resource and a Custom API that happen to share a name must not be matched to each other.
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(WebResource("shared", "h1")),
            State(CustomApi(new { uniqueName = "shared", isFunction = false }, "shared")));

        // The web resource is added (no live web resource), the Custom API is removed (no local Custom API).
        Assert.Contains(diff.Added, i => i.Kind == "web-resource");
        Assert.Contains(diff.Removed, i => i.Kind == "custom-api");
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Plugin_Only_State_Produces_No_Web_Resource_Diff()
    {
        // No web resources on either side: reconciling plugins never invents a web-resource add/remove.
        RegistrationDiff diff = DiffEngine.BuildDiff(
            State(Step(new { pluginTypeName = "P", stepKey = "k1" }, "P")),
            State());

        Assert.DoesNotContain(diff.Added.Concat(diff.Removed).Concat(diff.Changed.Select(c => new RegistrationItem(c.Kind, c.Type, default))), i => i.Kind == "web-resource");
    }
}
