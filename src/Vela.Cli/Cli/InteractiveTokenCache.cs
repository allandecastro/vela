using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Vela.Cli;

/// <summary>
/// Cross-platform persistence for the interactive browser-login MSAL token cache.
/// <para>
/// The previous default path delegated persistence to the Dataverse SDK's <c>TokenCacheStorePath</c>, which is
/// DPAPI-encrypted and <b>Windows-only</b>, so on macOS/Linux the token was never persisted and every
/// <c>--interactive</c> run re-opened the browser (BUG-7). This registers an
/// <see cref="MsalCacheHelper"/>-backed cache on the public client instead.
/// </para>
/// <para>
/// The cache is a file under the Vela config dir (<c>~/.vela</c> or <c>$VELA_CONFIG_DIR</c>): DPAPI-encrypted on
/// Windows, and a user-only plain file on macOS/Linux - the same trade the Azure CLI makes. A file in the config
/// dir (rather than a global OS keychain item) keeps the cache isolated per <c>$VELA_CONFIG_DIR</c> and lets a
/// single <c>vela logout</c> - or a test - remove it without mutating a process-wide secret store.
/// </para>
/// </summary>
internal static class InteractiveTokenCache
{
    /// <summary>The token-cache file name, under <see cref="UserConfig.ConfigDir"/>.</summary>
    internal const string CacheFileName = "msal.cache";

    /// <summary>
    /// Registers the persistent cache on the public client's user token cache, so a token acquired now is reused
    /// silently on the next run. Best-effort: if the cache cannot be set up, login still works - it just will not
    /// persist (the pre-fix behavior), so this never fails the command.
    /// </summary>
    public static void RegisterOn(IPublicClientApplication app)
    {
        try
        {
            CreateHelper().RegisterCache(app.UserTokenCache);
        }
        catch
        {
            // Best-effort: caching is a convenience, never a hard requirement for a single login.
        }
    }

    /// <summary>Removes the persisted token cache file (used by <c>vela logout</c> and <c>--fresh-login</c>). Best-effort.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(Path.Combine(UserConfig.ConfigDir, CacheFileName));
        }
        catch
        {
            // Best-effort: File.Delete is a no-op when the file is absent; ignore any other failure.
        }
    }

    private static MsalCacheHelper CreateHelper()
    {
        UserConfig.EnsureConfigDir();

        StorageCreationPropertiesBuilder builder = new StorageCreationPropertiesBuilder(CacheFileName, UserConfig.ConfigDir);

        // Windows: the default file storage is DPAPI-encrypted. macOS/Linux: use a user-only plain file rather
        // than the Keychain / libsecret keyring, so the cache stays scoped to the config dir and never triggers a
        // secure-store permission prompt.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder = builder.WithUnprotectedFile();
        }

        return MsalCacheHelper.CreateAsync(builder.Build()).GetAwaiter().GetResult();
    }
}
