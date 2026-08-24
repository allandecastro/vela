using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.JsonReader;

namespace Vela.Cli.Tests;

public class WebResourceScannerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(".html", 1)]
    [InlineData(".css", 2)]
    [InlineData(".js", 3)]
    [InlineData(".xml", 4)]
    [InlineData(".png", 5)]
    [InlineData(".jpg", 6)]
    [InlineData(".gif", 7)]
    [InlineData(".xsl", 9)]
    [InlineData(".ico", 10)]
    [InlineData(".svg", 11)]
    [InlineData(".resx", 12)]
    public void MapType_Maps_Known_Extensions(string extension, int expected)
    {
        Assert.Equal(expected, WebResourceScanner.MapType(extension));
    }

    [Theory]
    [InlineData(".xap")] // Silverlight, deprecated
    [InlineData(".txt")]
    [InlineData(".ts")]
    public void MapType_Returns_Null_For_Non_Deployable(string extension)
    {
        Assert.Null(WebResourceScanner.MapType(extension));
    }

    [Fact]
    public void Scan_Mirrors_The_Folder_Verbatim_With_Types_And_Hashes()
    {
        RunInTempDir(root =>
        {
            Write(root, "adc_/js/account/main.js", "console.log('a');");
            Write(root, "adc_/styles/theme.css", "body{}");
            Write(root, "adc_/imgs/logo.png", "PNGDATA");
            Write(root, "adc_/legacy/app.xap", "xap");   // skipped (deprecated)
            Write(root, "adc_/notes/readme.txt", "hi");  // skipped (unknown extension)

            var items = WebResourceScanner.Scan(root, Web);

            // Only the three deployable files, ordered by name.
            Assert.Equal(
                new[] { "adc_/imgs/logo.png", "adc_/js/account/main.js", "adc_/styles/theme.css" },
                items.Select(i => i.Type).ToArray());
            Assert.All(items, i => Assert.Equal("web-resource", i.Kind));

            RegistrationItem js = items.Single(i => i.Type == "adc_/js/account/main.js");
            Assert.Equal("adc_/js/account/main.js", TryGetString(js.Definition, "name"));
            Assert.Equal(3, TryGetInt(js.Definition, "webResourceType"));
            Assert.False(string.IsNullOrWhiteSpace(TryGetString(js.Definition, "contentHash")));
        });
    }

    [Fact]
    public void Scan_Hash_Changes_With_Content()
    {
        string hashA = HashOf("adc_/x.js", "A");
        string hashB = HashOf("adc_/x.js", "B");
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Scan_Missing_Folder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => WebResourceScanner.Scan(Path.Combine(Path.GetTempPath(), "vela-nope-" + Guid.NewGuid().ToString("N")), Web));
    }

    private static string HashOf(string relativePath, string content)
    {
        string? hash = null;
        RunInTempDir(root =>
        {
            Write(root, relativePath, content);
            hash = TryGetString(WebResourceScanner.Scan(root, Web).Single().Definition, "contentHash");
        });
        return hash!;
    }

    private static void Write(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void RunInTempDir(Action<string> action)
    {
        string temp = Path.Combine(Path.GetTempPath(), "vela-webres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            action(temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }
}
