using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vela.Cli;

/// <summary>
/// Pure drift check between the <c>Roles</c> class on disk and the security roles of the target environment, for
/// <c>vela models --verify --roles</c> / <c>--solution-roles</c>. The live roles go through the real
/// <see cref="RoleGenerator"/> in memory and both sides are reduced by <see cref="Extract"/> to the same shape (per
/// role: name, root id, grants), so only real differences surface, not formatting.
/// <para>
/// The <c>Roles</c> class is one file generated whole from the scope, so it is compared whole: a role the scope
/// resolves live but the class lacks, a role the class has but the environment does not, a different root id, and
/// any (table, privilege) whose access level differs or that only one side grants.
/// </para>
/// </summary>
internal static class RoleDriftChecker
{
    /// <summary>A grant the generated class records: the table, the privilege verb and the access level.</summary>
    public sealed record Grant(string Entity, string Privilege, string AccessLevel);

    /// <summary>A role read from a generated <c>Roles</c> class.</summary>
    public sealed record ExtractedRole(string Name, Guid Id, IReadOnlyList<Grant> Grants);

    /// <summary>A (table, privilege) both sides grant at a different access level.</summary>
    public sealed record LevelChange(string Entity, string Privilege, string LocalLevel, string LiveLevel);

    /// <summary>Drift for one role between the local <c>Roles</c> class and the environment.</summary>
    public sealed record RoleDrift(
        string Name,
        bool MissingInDataverse,
        bool MissingLocally,
        Guid? LocalId,
        Guid? LiveId,
        IReadOnlyList<Grant> RemovedInDataverse,
        IReadOnlyList<LevelChange> LevelChanged,
        IReadOnlyList<Grant> AddedInDataverse)
    {
        /// <summary>True when both sides have the role under different root ids.</summary>
        public bool IdChanged => LocalId is { } local && LiveId is { } live && local != live;

        /// <summary>True when this role is out of sync in any way.</summary>
        public bool HasDrift =>
            MissingInDataverse || MissingLocally || IdChanged
            || RemovedInDataverse.Count > 0 || LevelChanged.Count > 0 || AddedInDataverse.Count > 0;
    }

    /// <summary>The role drift across the verified scope; <see cref="MissingLocalClass"/> when no <c>Roles</c> class is on disk.</summary>
    public sealed record RoleDriftReport(bool MissingLocalClass, IReadOnlyList<RoleDrift> Roles)
    {
        /// <summary>True when the class is missing or at least one role is out of sync.</summary>
        public bool HasDrift => MissingLocalClass || Roles.Any(r => r.HasDrift);
    }

    /// <summary>
    /// The roles of a generated <c>Roles</c> class, or <c>null</c> when <paramref name="source"/> declares none (any
    /// other model file). Reads the <c>XName</c> constant, the <c>XId</c> Guid and the <c>X</c> grant triples of each role.
    /// </summary>
    public static IReadOnlyList<ExtractedRole>? Extract(string source)
    {
        if (string.IsNullOrEmpty(source) || source.IndexOf("public static class Roles", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        Dictionary<string, Guid> ids = new(StringComparer.Ordinal);
        Dictionary<string, List<Grant>> grants = new(StringComparer.Ordinal);
        List<string> order = [];
        List<Grant>? current = null;

        foreach (string raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (TryReadMember(line, "public const string ", out string identifier, out string rest)
                && identifier.EndsWith("Name", StringComparison.Ordinal)
                && ReadLiterals(rest) is [string name])
            {
                string role = identifier.Substring(0, identifier.Length - "Name".Length);
                names[role] = name;
                order.Add(role);
                current = null;
            }
            else if (TryReadMember(line, "public static readonly Guid ", out identifier, out rest)
                && identifier.EndsWith("Id", StringComparison.Ordinal)
                && ReadLiterals(rest) is [string id]
                && Guid.TryParse(id, out Guid guid))
            {
                ids[identifier.Substring(0, identifier.Length - "Id".Length)] = guid;
                current = null;
            }
            else if (TryReadMember(line, "public static readonly (string Entity, string Level, string Privilege)[] ", out identifier, out _))
            {
                current = [];
                grants[identifier] = current;
            }
            else if (current is not null && line.StartsWith("(\"", StringComparison.Ordinal)
                && ReadLiterals(line) is [string entity, string level, string privilege])
            {
                current.Add(new Grant(entity, privilege, level));
            }
            else if (line.StartsWith("};", StringComparison.Ordinal) || line.EndsWith("[0];", StringComparison.Ordinal))
            {
                current = null;
            }
        }

        return order
            .Select(role => new ExtractedRole(
                names[role],
                ids.TryGetValue(role, out Guid id) ? id : Guid.Empty,
                grants.TryGetValue(role, out List<Grant>? list) ? list : []))
            .ToList();
    }

    /// <summary>
    /// Compares the <c>Roles</c> class generated from the live roles (<paramref name="liveSource"/>, null when none
    /// resolved) against the local model sources, and lists each name in <paramref name="notFoundInDataverse"/> (a
    /// requested role the environment does not have) as missing there.
    /// </summary>
    public static RoleDriftReport Compare(string? liveSource, IEnumerable<string> localSources, IEnumerable<string> notFoundInDataverse)
    {
        IReadOnlyList<ExtractedRole> live = (liveSource is null ? null : Extract(liveSource)) ?? [];
        IReadOnlyList<ExtractedRole>? local = localSources.Select(Extract).FirstOrDefault(r => r is not null);
        Dictionary<string, ExtractedRole> localByName = (local ?? [])
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> liveNames = new(live.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        List<RoleDrift> drifts = [];
        foreach (ExtractedRole liveRole in live)
        {
            if (!localByName.TryGetValue(liveRole.Name, out ExtractedRole? localRole))
            {
                drifts.Add(new RoleDrift(liveRole.Name, MissingInDataverse: false, MissingLocally: true, null, liveRole.Id, [], [], []));
                continue;
            }

            drifts.Add(CompareGrants(localRole, liveRole));
        }

        // A role the class has but the environment does not (a requested name, or one the solution no longer holds).
        IEnumerable<string> localOnly = localByName.Keys.Where(n => !liveNames.Contains(n));
        foreach (string name in localOnly.Concat(notFoundInDataverse).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (liveNames.Contains(name))
            {
                continue;
            }

            Guid? localId = localByName.TryGetValue(name, out ExtractedRole? localRole) ? localRole.Id : null;
            drifts.Add(new RoleDrift(localRole?.Name ?? name, MissingInDataverse: true, MissingLocally: localRole is null, localId, null, [], [], []));
        }

        return new RoleDriftReport(
            MissingLocalClass: local is null,
            drifts.OrderBy(d => d.Name, StringComparer.Ordinal).ToList());
    }

    private static RoleDrift CompareGrants(ExtractedRole local, ExtractedRole live)
    {
        Dictionary<(string, string), Grant> localGrants = Index(local.Grants);
        Dictionary<(string, string), Grant> liveGrants = Index(live.Grants);

        List<Grant> removed = [];
        List<LevelChange> changed = [];
        foreach (Grant grant in Sorted(localGrants.Values))
        {
            if (!liveGrants.TryGetValue((grant.Entity, grant.Privilege), out Grant? liveGrant))
            {
                removed.Add(grant);
            }
            else if (!string.Equals(grant.AccessLevel, liveGrant.AccessLevel, StringComparison.Ordinal))
            {
                changed.Add(new LevelChange(grant.Entity, grant.Privilege, grant.AccessLevel, liveGrant.AccessLevel));
            }
        }

        List<Grant> added = Sorted(liveGrants.Values.Where(g => !localGrants.ContainsKey((g.Entity, g.Privilege)))).ToList();
        return new RoleDrift(live.Name, MissingInDataverse: false, MissingLocally: false, local.Id, live.Id, removed, changed, added);
    }

    // RolePrivilegeMapper keeps one grant per (table, privilege); a hand-edited duplicate keeps its first entry.
    private static Dictionary<(string, string), Grant> Index(IEnumerable<Grant> grants)
    {
        Dictionary<(string, string), Grant> map = [];
        foreach (Grant grant in grants)
        {
            map.TryAdd((grant.Entity, grant.Privilege), grant);
        }

        return map;
    }

    private static IEnumerable<Grant> Sorted(IEnumerable<Grant> grants) =>
        grants.OrderBy(g => g.Entity, StringComparer.Ordinal).ThenBy(g => g.Privilege, StringComparer.Ordinal);

    // "public const string SalesRepName = ..." -> ("SalesRep", "= ...").
    private static bool TryReadMember(string line, string prefix, out string identifier, out string rest)
    {
        identifier = string.Empty;
        rest = string.Empty;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int end = prefix.Length;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }

        identifier = line.Substring(prefix.Length, end - prefix.Length);
        rest = line.Substring(end);
        return identifier.Length > 0;
    }

    // The regular string literals of a line, unescaped (the generator escapes \\, \", \r and \n).
    private static List<string> ReadLiterals(string line)
    {
        List<string> literals = [];
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '"')
            {
                continue;
            }

            StringBuilder value = new();
            for (i++; i < line.Length && line[i] != '"'; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    i++;
                    value.Append(line[i] switch { 'r' => '\r', 'n' => '\n', char c => c });
                }
                else
                {
                    value.Append(line[i]);
                }
            }

            literals.Add(value.ToString());
        }

        return literals;
    }
}
