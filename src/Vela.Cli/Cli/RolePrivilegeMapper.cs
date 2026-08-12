using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Cli;

/// <summary>
/// Maps Dataverse role privileges (privilege id + depth mask) to the harness's <c>RoleGrantModel</c>
/// (table, <c>Privilege</c> verb, <c>AccessLevel</c>). Pure — the metadata it needs is resolved by
/// <c>ModelsCommand</c> and passed in.
/// </summary>
public static class RolePrivilegeMapper
{
    // Access-level names as emitted (Vela.Testing.Security.AccessLevel members). Ordered by increasing depth so
    // the highest granted level can be picked when a (table, verb) appears more than once.
    private static readonly string[] LevelsByRank = { "User", "BusinessUnit", "ParentChild", "Organization" };

    /// <summary>The <c>AccessLevel</c> name for a Dataverse depth mask (highest bit wins), or null for no access.</summary>
    public static string? AccessLevel(int depthMask)
    {
        if ((depthMask & 8) != 0)
        {
            return "Organization"; // Global
        }

        if ((depthMask & 4) != 0)
        {
            return "ParentChild"; // Deep
        }

        if ((depthMask & 2) != 0)
        {
            return "BusinessUnit"; // Local
        }

        if ((depthMask & 1) != 0)
        {
            return "User"; // Basic
        }

        return null;
    }

    /// <summary>The <c>Privilege</c> verb name for an entity <see cref="PrivilegeType"/>, or null for non-entity types.</summary>
    public static string? Verb(PrivilegeType type)
    {
        switch (type)
        {
            case PrivilegeType.Create: return "Create";
            case PrivilegeType.Read: return "Read";
            case PrivilegeType.Write: return "Write";
            case PrivilegeType.Delete: return "Delete";
            case PrivilegeType.Append: return "Append";
            case PrivilegeType.AppendTo: return "AppendTo";
            case PrivilegeType.Assign: return "Assign";
            case PrivilegeType.Share: return "Share";
            default: return null; // None or a non-entity/miscellaneous privilege — skipped
        }
    }

    /// <summary>
    /// Turns a role's privileges into grants, resolving each privilege id to its (table, type) via
    /// <paramref name="privileges"/>. Privileges not on any table (miscellaneous) or with no access are skipped;
    /// a (table, verb) that appears more than once keeps the highest access level.
    /// </summary>
    public static IReadOnlyList<RoleGrantModel> ToGrants(
        IEnumerable<(Guid PrivilegeId, int DepthMask)> rolePrivileges,
        IReadOnlyDictionary<Guid, (string Entity, PrivilegeType Type)> privileges)
    {
        Dictionary<(string Entity, string Verb), string> highest = new Dictionary<(string, string), string>();

        foreach ((Guid privilegeId, int depthMask) in rolePrivileges)
        {
            if (!privileges.TryGetValue(privilegeId, out (string Entity, PrivilegeType Type) privilege))
            {
                continue;
            }

            string? verb = Verb(privilege.Type);
            string? level = AccessLevel(depthMask);
            if (verb == null || level == null || string.IsNullOrWhiteSpace(privilege.Entity))
            {
                continue;
            }

            var key = (privilege.Entity, verb);
            if (!highest.TryGetValue(key, out string? existing) || Rank(level) > Rank(existing))
            {
                highest[key] = level;
            }
        }

        return highest
            .OrderBy(g => g.Key.Entity, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Verb, StringComparer.Ordinal)
            .Select(g => new RoleGrantModel(g.Key.Entity, g.Key.Verb, g.Value))
            .ToList();
    }

    private static int Rank(string level) => Array.IndexOf(LevelsByRank, level);
}
