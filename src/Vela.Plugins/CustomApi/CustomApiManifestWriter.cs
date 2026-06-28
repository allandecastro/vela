using System;
using Newtonsoft.Json;

namespace Vela.CustomApi;

/// <summary>
/// Serializes Custom API manifests.
/// </summary>
public static class CustomApiManifestWriter
{
    /// <summary>
    /// Converts a manifest to JSON.
    /// </summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>Indented JSON.</returns>
    public static string ToJson(CustomApiManifest manifest)
    {
        if (manifest == null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        return JsonConvert.SerializeObject(manifest, Formatting.Indented);
    }
}
