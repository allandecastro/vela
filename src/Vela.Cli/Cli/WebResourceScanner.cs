using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vela.Cli;

/// <summary>The scanned shape of one web resource file: its Dataverse name, type, content hash and local path.</summary>
public sealed record WebResourceDefinition(string Name, int WebResourceType, string ContentHash, string ContentPath);

/// <summary>
/// Walks a web resources root folder and turns each file into a <see cref="RegistrationItem"/> of kind
/// <c>web-resource</c> (parallel to <see cref="PackageScanner"/> for plugins). The unique name is the file path
/// relative to the root, VERBATIM with forward slashes (the top-level folder, e.g. <c>adc_</c>, is a real folder,
/// not a prepended prefix). The type is inferred from the extension; a content hash drives the diff. Pure: no
/// Dataverse I/O.
/// </summary>
internal static class WebResourceScanner
{
    // Extension -> webresourcetype. .xap (8, Silverlight) is deprecated and skipped; unknown extensions are skipped.
    private static readonly IReadOnlyDictionary<string, int> TypeByExtension = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [".htm"] = 1,
        [".html"] = 1,
        [".css"] = 2,
        [".js"] = 3,
        [".xml"] = 4,
        [".png"] = 5,
        [".jpg"] = 6,
        [".jpeg"] = 6,
        [".gif"] = 7,
        [".xsl"] = 9,
        [".xslt"] = 9,
        [".ico"] = 10,
        [".svg"] = 11,
        [".resx"] = 12,
    };

    /// <summary>Maps a file extension to its <c>webresourcetype</c>, or null when the extension is not deployable.</summary>
    public static int? MapType(string extension) =>
        TypeByExtension.TryGetValue(extension, out int type) ? type : null;

    public static IReadOnlyList<RegistrationItem> Scan(string root, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A web resources root folder is required.", nameof(root));
        }

        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Web resources folder not found: {fullRoot}");
        }

        List<RegistrationItem> items = new List<RegistrationItem>();
        foreach (string file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            int? type = MapType(Path.GetExtension(file));
            if (type == null)
            {
                continue; // unknown / deprecated extension: not a deployable web resource
            }

            string name = ToWebResourceName(fullRoot, file);
            WebResourceDefinition definition = new WebResourceDefinition(name, type.Value, HashFile(file), file);
            items.Add(new RegistrationItem("web-resource", name, JsonSerializer.SerializeToElement(definition, jsonOptions)));
        }

        // Deterministic order by the web resource name so plan output and apply order are stable.
        return items.OrderBy(i => i.Type, StringComparer.Ordinal).ToList();
    }

    // The web resource unique name: the path relative to the root, verbatim, with forward slashes.
    private static string ToWebResourceName(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    private static string HashFile(string file)
    {
        using FileStream stream = File.OpenRead(file);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
