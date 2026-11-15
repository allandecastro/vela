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

    /// <summary>A role read from a generated <c>Roles</c> class; <see cref="TemplateId"/> is set for a system role only.</summary>
    public sealed record ExtractedRole(string Name, Guid Id, IReadOnlyList<Grant> Grants, Guid? TemplateId = null);

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
        IReadOnlyList<Grant> AddedInDataverse,
        Guid? LocalTemplateId = null,
        Guid? LiveTemplateId = null)
    {
        /// <summary>True when both sides have the role under different root ids.</summary>
        public bool IdChanged => LocalId is { } local && LiveId is { } live && local != live;

        /// <summary>True when both sides have the role and their template ids (roletemplateid, system roles only) differ.</summary>
        public bool TemplateIdChanged => !MissingInDataverse && !MissingLocally && LocalTemplateId != LiveTemplateId;

        /// <summary>True when this role is out of sync in any way.</summary>
        public bool HasDrift =>
            MissingInDataverse || MissingLocally || IdChanged || TemplateIdChanged
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
    /// other model file). Reads the <c>XName</c> constant, the <c>XId</c> Guid, the <c>XTemplateId</c> Guid of a system
    /// role and the <c>X</c> grant triples of each role. A member belongs to the role whose <c>XName</c> precedes it,
    /// matched on its exact identifier, so <c>XTemplateId</c> is never read as the id of a role named "X Template".
    /// </summary>
    public static IReadOnlyList<ExtractedRole>? Extract(string source)
    {
        if (string.IsNullOrEmpty(source) || source.IndexOf("public static class Roles", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        List<(string Identifier, string Name)> order = [];
        Dictionary<string, Guid> ids = new(StringComparer.Ordinal);
        Dictionary<string, Guid> templateIds = new(StringComparer.Ordinal);
        Dictionary<string, List<Grant>> grants = new(StringComparer.Ordinal);
        string? role = null;
        List<Grant>? current = null;

        foreach (string raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (TryReadMember(line, "public const string ", out string identifier, out string rest)
                && identifier.EndsWith("Name", StringComparison.Ordinal)
                && ReadLiterals(rest) is [string name])
            {
                role = identifier.Substring(0, identifier.Length - "Name".Length);
                order.Add((role, name));
                current = null;
            }
            else if (role is not null
                && TryReadMember(line, "public static readonly Guid ", out identifier, out rest)
                && ReadLiterals(rest) is [string value]
                && Guid.TryParse(value, out Guid guid))
            {
                if (identifier == role + "Id")
                {
                    ids[role] = guid;
                }
                else if (identifier == role + "TemplateId")
                {
                    templateIds[role] = guid;
                }

                current = null;
            }
            else if (role is not null
                && TryReadMember(line, "public static readonly (string Entity, string Level, string Privilege)[] ", out identifier, out _)
                && identifier == role)
            {
                current = [];
                grants[role] = current;
            }
            else if (current is not null && IsTupleStart(line)
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
            .Select(r => new ExtractedRole(
                r.Name,
                ids.TryGetValue(r.Identifier, out Guid id) ? id : Guid.Empty,
                grants.TryGetValue(r.Identifier, out List<Grant>? list) ? list : [],
                templateIds.TryGetValue(r.Identifier, out Guid template) ? template : null))
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
        ILookup<string, ExtractedRole> localByName = (local ?? []).ToLookup(r => r.Name, StringComparer.OrdinalIgnoreCase);
        HashSet<string> liveNames = new(live.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        // Several root roles can share a name (the generator suffixes their identifiers), so each name keeps all its
        // roles on both sides and they are paired by root id first.
        List<RoleDrift> drifts = [];
        foreach (IGrouping<string, ExtractedRole> liveGroup in live.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            List<ExtractedRole> localLeft = localByName[liveGroup.Key].ToList();
            List<ExtractedRole> liveLeft = [];
            foreach (ExtractedRole liveRole in liveGroup)
            {
                int match = localLeft.FindIndex(r => r.Id == liveRole.Id);
                if (match < 0)
                {
                    liveLeft.Add(liveRole);
                    continue;
                }

                drifts.Add(CompareGrants(localLeft[match], liveRole));
                localLeft.RemoveAt(match);
            }

            // One role left on each side is the same role under another root id. More than one cannot be paired
            // reliably, so each is reported on its own: missing locally or missing in the environment.
            if (liveLeft.Count == 1 && localLeft.Count == 1)
            {
                drifts.Add(CompareGrants(localLeft[0], liveLeft[0]));
                continue;
            }

            drifts.AddRange(liveLeft.Select(MissingLocally));
            drifts.AddRange(localLeft.Select(MissingInDataverse));
        }

        // A role the class has but the environment does not (one the solution no longer holds), then a requested
        // name neither side has.
        drifts.AddRange((local ?? []).Where(r => !liveNames.Contains(r.Name)).Select(MissingInDataverse));
        foreach (string name in notFoundInDataverse.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!liveNames.Contains(name) && !localByName.Contains(name))
            {
                drifts.Add(new RoleDrift(name, MissingInDataverse: true, MissingLocally: true, null, null, [], [], []));
            }
        }

        return new RoleDriftReport(
            MissingLocalClass: local is null,
            drifts.OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.LiveId ?? d.LocalId).ToList());
    }

    private static RoleDrift MissingLocally(ExtractedRole live) =>
        new(live.Name, MissingInDataverse: false, MissingLocally: true, null, live.Id, [], [], []);

    private static RoleDrift MissingInDataverse(ExtractedRole local) =>
        new(local.Name, MissingInDataverse: true, MissingLocally: false, local.Id, null, [], [], []);

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
        return new RoleDrift(live.Name, MissingInDataverse: false, MissingLocally: false, local.Id, live.Id, removed, changed, added, local.TemplateId, live.TemplateId);
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

    // A grant tuple line: "(", optional whitespace, then the first string literal ("( \"account\", ..." included).
    private static bool IsTupleStart(string line)
    {
        if (!line.StartsWith("(", StringComparison.Ordinal))
        {
            return false;
        }

        int i = 1;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        return i < line.Length && line[i] == '"';
    }

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
