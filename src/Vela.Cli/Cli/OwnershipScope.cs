using static Vela.Cli.JsonReader;

namespace Vela.Cli;

/// <summary>
/// Scopes the live state to what Vela owns. The live read takes every plugin type of the deployed assembly, but an
/// assembly can also hold classic <c>IPlugin</c> / <c>CodeActivity</c> classes registered with other tools (the Plugin
/// Registration Tool, a solution import). Their steps and Custom APIs are not in the local scan, so the diff would see
/// them as "removed" and apply would delete them. Excluding them leaves them untouched; they are reported as info,
/// never as a warning (so <c>--strict</c> does not fail on them).
/// A type that is no longer in the assembly at all is still in scope, so the steps of a deleted Vela plugin are removed.
/// </summary>
internal static class OwnershipScope
{
    /// <summary>
    /// Removes the items implemented by <paramref name="unmanagedPluginTypes"/> from <paramref name="current"/> and
    /// describes each excluded type (with its step and Custom API counts) in <paramref name="unmanaged"/>.
    /// </summary>
    internal static RegistrationState ExcludeUnmanaged(RegistrationState current, IReadOnlyCollection<string>? unmanagedPluginTypes, out string[] unmanaged)
    {
        if (unmanagedPluginTypes is not { Count: > 0 })
        {
            unmanaged = [];
            return current;
        }

        HashSet<string> types = new(unmanagedPluginTypes, StringComparer.OrdinalIgnoreCase);
        List<RegistrationItem> kept = [];
        Dictionary<string, (int Steps, int CustomApis)> excluded = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistrationItem item in current.Items)
        {
            string pluginType = TryGetString(item.Definition, "pluginTypeName") ?? item.Type;
            if (!types.Contains(pluginType))
            {
                kept.Add(item);
                continue;
            }

            (int steps, int apis) = excluded.TryGetValue(pluginType, out var counts) ? counts : (0, 0);
            excluded[pluginType] = DiffEngine.IsStep(item) ? (steps + 1, apis) : (steps, apis + 1);
        }

        unmanaged = excluded
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key} is not a Vela plugin type; its {Describe(x.Value.Steps, x.Value.CustomApis)} are left untouched.")
            .ToArray();
        return current with { Items = [.. kept] };
    }

    private static string Describe(int steps, int customApis) => (steps, customApis) switch
    {
        (> 0, > 0) => $"{steps} step(s) and {customApis} Custom API(s)",
        (> 0, _) => $"{steps} step(s)",
        _ => $"{customApis} Custom API(s)",
    };
}
