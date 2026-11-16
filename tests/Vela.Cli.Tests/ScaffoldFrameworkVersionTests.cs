using System.Net;
using System.Text;
using Xunit;

namespace Vela.Cli.Tests;

// `vela init` resolves the framework version from the nuget.org registration index: the newest listed, stable
// version on the release line of ScaffoldOptions.DefaultCoreVersion, else null (the caller falls back to the default).
public class ScaffoldFrameworkVersionTests
{
    private static readonly Version Line = Version.Parse(ScaffoldOptions.DefaultCoreVersion);

    // Versions relative to DefaultCoreVersion, so the tests survive its bump: same minor, next minor, next major.
    private static string Same(int patch) => $"{Line.Major}.{Line.Minor}.{patch}";
    private static string NextMinor => $"{Line.Major}.{Line.Minor + 1}.0";
    private static string NextMajor => $"{Line.Major + 1}.0.0";

    private sealed class FakeFeed(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(responses.TryGetValue(url, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Leaf(string version, bool? listed)
    {
        string listedProperty = listed is null ? "" : $", \"listed\": {(listed.Value ? "true" : "false")}";
        return $"{{ \"catalogEntry\": {{ \"version\": \"{version}\"{listedProperty} }} }}";
    }

    private static string InlinePage(params string[] leaves) => $"{{ \"items\": [{string.Join(", ", leaves)}] }}";

    private static string Index(params string[] pages) => $"{{ \"count\": {pages.Length}, \"items\": [{string.Join(", ", pages)}] }}";

    private static string? Resolve(string index, Dictionary<string, string>? pages = null)
    {
        Dictionary<string, string> responses = new(pages ?? []) { [ScaffoldOptions.FrameworkRegistrationUrl] = index };
        using FakeFeed feed = new(responses);
        return ScaffoldOptions.ResolveLatestFrameworkVersion(feed);
    }

    [Fact]
    public void Resolve_SkipsAnUnlistedVersion()
    {
        string? version = Resolve(Index(InlinePage(Leaf(Same(0), listed: true), Leaf(Same(1), listed: null), Leaf(Same(2), listed: false))));

        // 2 is unlisted; 1 has no `listed` property, which means listed.
        Assert.Equal(Same(1), version);
    }

    [Fact]
    public void Resolve_IgnoresANewerMinorOrMajor()
    {
        string? version = Resolve(Index(InlinePage(Leaf(Same(3), true), Leaf(NextMinor, true), Leaf(NextMajor, true))));

        Assert.Equal(Same(3), version);
    }

    [Fact]
    public void Resolve_SkipsPrereleases_AndDropsBuildMetadata()
    {
        string? version = Resolve(Index(InlinePage(Leaf(Same(1) + "+abc123", true), Leaf(Same(2) + "-preview.1", true))));

        Assert.Equal(Same(1), version);
    }

    [Fact]
    public void Resolve_ReadsAPageTheIndexOnlyLinks()
    {
        const string pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/vela.plugins/page/0.0.1/9.9.9.json";
        string index = Index(InlinePage(Leaf(Same(0), true)), $"{{ \"@id\": \"{pageUrl}\", \"count\": 1 }}");

        string? version = Resolve(index, new Dictionary<string, string> { [pageUrl] = InlinePage(Leaf(Same(4), true)) });

        Assert.Equal(Same(4), version);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoListedVersionIsCompatible()
    {
        // The caller then falls back to DefaultCoreVersion.
        Assert.Null(Resolve(Index(InlinePage(Leaf(NextMinor, true), Leaf(Same(5), false)))));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTheFeedFails()
    {
        using FakeFeed feed = new([]); // every request is a 404
        Assert.Null(ScaffoldOptions.ResolveLatestFrameworkVersion(feed));
        Assert.Equal([ScaffoldOptions.FrameworkRegistrationUrl], feed.Requested);
    }

    [Fact]
    public void Resolve_ReturnsNull_OnAMalformedIndex()
    {
        Assert.Null(Resolve("{ \"versions\": [\"0.1.0\"] }"));
    }

    [Theory]
    [InlineData("0.43.0", "0.43.2")]
    [InlineData("1.2.0", "1.9.0")] // from 1.0, the same major is compatible
    public void SelectFrameworkVersion_KeepsTheReleaseLine(string compatibleWith, string expected)
    {
        (string, bool)[] published = [("0.42.9", true), ("0.43.1", true), ("0.43.2", true), ("0.44.0", true), ("1.0.0", true), ("1.9.0", true), ("2.0.0", true)];

        Assert.Equal(expected, ScaffoldOptions.SelectFrameworkVersion(published, compatibleWith));
    }
}
