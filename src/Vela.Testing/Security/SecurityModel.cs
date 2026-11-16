using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// A Dataverse table privilege. Maps one-to-one to the platform's privilege verbs; a
    /// <see cref="SecurityConfiguration"/> grants these at an <see cref="AccessLevel"/> depth.
    /// </summary>
    public enum Privilege
    {
        /// <summary>Create records of the table.</summary>
        Create,

        /// <summary>Read records of the table.</summary>
        Read,

        /// <summary>Update records of the table.</summary>
        Write,

        /// <summary>Delete records of the table.</summary>
        Delete,

        /// <summary>Associate another record TO this one (the child side of a relationship).</summary>
        Append,

        /// <summary>Associate this record to another (the parent side of a relationship).</summary>
        AppendTo,

        /// <summary>Change the owner of a record.</summary>
        Assign,

        /// <summary>Share a record with another principal.</summary>
        Share,
    }

    /// <summary>
    /// The depth at which a <see cref="Privilege"/> is granted, mirroring Dataverse's access levels
    /// (Basic/Local/Deep/Global). A grant covers a record when its level reaches the record's scope.
    /// </summary>
    public enum AccessLevel
    {
        /// <summary>No access.</summary>
        None,

        /// <summary>Basic: only records the user owns (directly, or via a team the user belongs to).</summary>
        User,

        /// <summary>Local: records owned within the user's business unit.</summary>
        BusinessUnit,

        /// <summary>Deep: records owned within the user's business unit and its descendants.</summary>
        ParentChild,

        /// <summary>Global: every record, regardless of owner or business unit.</summary>
        Organization,
    }

    /// <summary>
    /// How a team's members inherit its roles (the team's "Member's privilege inheritance" setting).
    /// </summary>
    public enum TeamPrivilegeInheritance
    {
        /// <summary>Team and user privileges: members use the team's roles as their own (the default).</summary>
        MemberAndTeam,

        /// <summary>
        /// Team privileges only: a member can use the team's <see cref="AccessLevel.User"/>-level grants only for
        /// records owned by the team, not for their own records. Higher levels are unaffected.
        /// </summary>
        TeamOnly,
    }

    /// <summary>
    /// Fluent builder for a named security role: a bag of (table, privilege, level) grants that can be
    /// assigned to users and teams via <see cref="SecurityConfiguration"/>.
    /// </summary>
    public sealed class RoleBuilder
    {
        internal RoleBuilder()
        {
        }

        // (entity, privilege) -> highest level granted. A role never lowers a privilege it already grants.
        internal Dictionary<(string Entity, Privilege Privilege), AccessLevel> Grants { get; } =
            new Dictionary<(string, Privilege), AccessLevel>();

        /// <summary>
        /// Grants one or more <paramref name="privileges"/> on <paramref name="entity"/> at <paramref name="level"/>.
        /// Argument order is always (entity, level, privileges) — e.g. <c>Grant("account", AccessLevel.User, Privilege.Read)</c>.
        /// </summary>
        public RoleBuilder Grant(string entity, AccessLevel level, params Privilege[] privileges)
        {
            if (string.IsNullOrWhiteSpace(entity))
            {
                throw new ArgumentException("Entity logical name is required.", nameof(entity));
            }

            foreach (Privilege privilege in privileges ?? Array.Empty<Privilege>())
            {
                // Entity logical names are case-insensitive across the harness, so normalize the key to match.
                var key = (entity.ToLowerInvariant(), privilege);
                if (!Grants.TryGetValue(key, out AccessLevel existing) || level > existing)
                {
                    Grants[key] = level;
                }
            }

            return this;
        }
    }

    /// <summary>
    /// Declares an opt-in security scenario for <c>InMemoryOrganizationService.WithSecurity</c>: business units
    /// (with an optional parent, forming a hierarchy), named roles, users and teams (each in a business unit and
    /// holding roles), and team membership. When configured, the service enforces table privileges on
    /// Create/Retrieve/Update/Delete and filters RetrieveMultiple to readable records. Off by default: with no
    /// security configured the service grants full access, exactly as before.
    /// </summary>
    public sealed class SecurityConfiguration
    {
        private readonly Dictionary<Guid, Guid> _businessUnitParents = new Dictionary<Guid, Guid>();
        private readonly Dictionary<string, RoleBuilder> _roles = new Dictionary<string, RoleBuilder>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Principal> _users = new Dictionary<Guid, Principal>();
        private readonly Dictionary<Guid, Principal> _teams = new Dictionary<Guid, Principal>();
        private readonly Dictionary<Guid, HashSet<Guid>> _memberships = new Dictionary<Guid, HashSet<Guid>>();
        private readonly Dictionary<string, Guid> _roleIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid> _roleTemplateIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Guid> _managers = new Dictionary<Guid, Guid>();
        private readonly Dictionary<Guid, Guid> _positions = new Dictionary<Guid, Guid>();
        private readonly Dictionary<Guid, Guid> _positionParents = new Dictionary<Guid, Guid>();
        private int _hierarchyDepth;
        private bool _managerHierarchy;
        private bool _positionHierarchy;
        private readonly HashSet<(string Entity, string Column)> _securedColumns = new HashSet<(string, string)>();
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldRead = new HashSet<(Guid, string, string)>();
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldUpdate = new HashSet<(Guid, string, string)>();

        /// <summary>Declares a business unit, optionally as a child of <paramref name="parent"/> (a BU hierarchy).</summary>
        public SecurityConfiguration BusinessUnit(Guid id, Guid parent = default)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Business unit id is required.", nameof(id));
            }

            _businessUnitParents[id] = parent;
            return this;
        }

        /// <summary>Defines a named role from a set of grants. Assign it to users/teams by name.</summary>
        public SecurityConfiguration Role(string name, Action<RoleBuilder> configure) =>
            Role(name, DeterministicGuid.From("role", name), configure);

        /// <summary>
        /// Defines a named role with an explicit root <paramref name="id"/> (e.g. a generated <c>Roles.XId</c>). The
        /// materialized <c>role</c> rows carry it as <c>parentrootroleid</c> on every business unit copy, and the root
        /// business unit's copy keeps it as its <c>roleid</c>. Without an id one is derived from the name.
        /// </summary>
        public SecurityConfiguration Role(string name, Guid id, Action<RoleBuilder> configure)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Role name is required.", nameof(name));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            RoleBuilder builder = new RoleBuilder();
            configure(builder);
            _roles[name] = builder;
            _roleIds[name] = id;
            _roleTemplateIds.Remove(name);
            return this;
        }

        /// <summary>
        /// Defines a named system role with an explicit root <paramref name="id"/> and its
        /// <paramref name="templateId"/> (<c>roletemplateid</c>, e.g. a generated <c>Roles.XTemplateId</c>), written on
        /// every business unit copy of the materialized <c>role</c> rows. Roles declared without one have no template.
        /// </summary>
        public SecurityConfiguration Role(string name, Guid id, Guid templateId, Action<RoleBuilder> configure) =>
            Role(name, id, configure).RoleTemplate(name, templateId);

        /// <summary>
        /// Defines a named system role from generated grant data with an explicit root <paramref name="id"/> and its
        /// <paramref name="templateId"/> (<c>roletemplateid</c>, a generated <c>Roles.XTemplateId</c>), written on
        /// every business unit copy of the materialized <c>role</c> rows.
        /// </summary>
        public SecurityConfiguration Role(string name, Guid id, Guid templateId, params (string Entity, string Level, string Privilege)[] grants) =>
            Role(name, id, grants).RoleTemplate(name, templateId);

        private SecurityConfiguration RoleTemplate(string name, Guid templateId)
        {
            if (templateId == Guid.Empty)
            {
                throw new ArgumentException("Role template id is required.", nameof(templateId));
            }

            _roleTemplateIds[name] = templateId;
            return this;
        }

        /// <summary>
        /// Defines a named role from generated grant data (a <c>Roles.X</c> array of
        /// <c>(entity, access level, privilege)</c> name triples) with an explicit root <paramref name="id"/>. The
        /// level/privilege names must match <see cref="AccessLevel"/>/<see cref="Privilege"/> members. This is the
        /// overload the dependency-light generated <c>Roles</c> class feeds; hand-written roles use the
        /// <see cref="Action{RoleBuilder}"/> overloads.
        /// </summary>
        public SecurityConfiguration Role(string name, Guid id, params (string Entity, string Level, string Privilege)[] grants)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Role name is required.", nameof(name));
            }

            RoleBuilder builder = new RoleBuilder();
            foreach ((string entity, string level, string privilege) in grants ?? Array.Empty<(string, string, string)>())
            {
                // Enum.IsDefined on the string validates the exact member name (case-sensitive) and rejects numeric
                // strings like "2" — unlike Enum.TryParse, which would accept them.
                if (level == null || !Enum.IsDefined(typeof(AccessLevel), level))
                {
                    throw new ArgumentException($"Unknown access level '{level}' in role '{name}'.", nameof(grants));
                }

                if (privilege == null || !Enum.IsDefined(typeof(Privilege), privilege))
                {
                    throw new ArgumentException($"Unknown privilege '{privilege}' in role '{name}'.", nameof(grants));
                }

                AccessLevel accessLevel = (AccessLevel)Enum.Parse(typeof(AccessLevel), level);
                Privilege verb = (Privilege)Enum.Parse(typeof(Privilege), privilege);
                builder.Grant(entity, accessLevel, verb);
            }

            _roles[name] = builder;
            _roleIds[name] = id;
            _roleTemplateIds.Remove(name);
            return this;
        }

        /// <summary>Registers a user in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration User(Guid id, Guid businessUnit, params string[] roles)
        {
            _users[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>());
            return this;
        }

        /// <summary>Registers a team in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration Team(Guid id, Guid businessUnit, params string[] roles) =>
            Team(id, businessUnit, TeamPrivilegeInheritance.MemberAndTeam, roles);

        /// <summary>
        /// Registers a team with an explicit <paramref name="inheritance"/> mode. With
        /// <see cref="TeamPrivilegeInheritance.TeamOnly"/>, members use the team's Basic-level grants only for
        /// records owned by the team.
        /// </summary>
        public SecurityConfiguration Team(Guid id, Guid businessUnit, TeamPrivilegeInheritance inheritance, params string[] roles)
        {
            _teams[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>(), inheritance);
            return this;
        }

        /// <summary>
        /// Enables **manager hierarchy** security to <paramref name="depth"/> levels. A manager gets read/write on a
        /// direct report's records and read-only on deeper reports' records (up to depth), provided the manager is in
        /// the report's business unit or its parent. Set the reporting links with <see cref="Manager"/>.
        /// </summary>
        public SecurityConfiguration ManagerHierarchy(int depth = 1)
        {
            _managerHierarchy = true;
            _hierarchyDepth = depth < 0 ? 0 : depth;
            return this;
        }

        /// <summary>Declares that <paramref name="userId"/> reports to <paramref name="managerId"/> (manager hierarchy).</summary>
        public SecurityConfiguration Manager(Guid userId, Guid managerId)
        {
            _managers[userId] = managerId;
            return this;
        }

        /// <summary>
        /// Enables **position hierarchy** security to <paramref name="depth"/> levels. Unlike the manager hierarchy,
        /// a higher position reaches lower positions' records in the direct ancestor path <b>across business units</b>
        /// (no BU constraint). A direct-higher position gets read/write; non-direct higher positions get read-only.
        /// Tag users with <see cref="Position"/> and shape the position tree with <see cref="PositionParent"/>.
        /// Dataverse activates only one hierarchy model at a time, so configuring both this and
        /// <see cref="ManagerHierarchy"/> throws when the configuration is built.
        /// </summary>
        public SecurityConfiguration PositionHierarchy(int depth = 1)
        {
            _positionHierarchy = true;
            _hierarchyDepth = depth < 0 ? 0 : depth;
            return this;
        }

        /// <summary>Tags <paramref name="userId"/> with <paramref name="positionId"/> (a user holds one position).</summary>
        public SecurityConfiguration Position(Guid userId, Guid positionId)
        {
            _positions[userId] = positionId;
            return this;
        }

        /// <summary>Sets <paramref name="parentPositionId"/> as the parent of <paramref name="positionId"/> in the position tree.</summary>
        public SecurityConfiguration PositionParent(Guid positionId, Guid parentPositionId)
        {
            _positionParents[positionId] = parentPositionId;
            return this;
        }

        /// <summary>
        /// Marks a column as **field-secured**: even with record access, it is hidden on read and rejected on write
        /// unless a <see cref="FieldPermission"/> grants it. (Field-level security.)
        /// </summary>
        public SecurityConfiguration SecureColumn(string entity, string column)
        {
            _securedColumns.Add((Norm(entity), Norm(column)));
            return this;
        }

        /// <summary>Grants a principal (user or team) read and/or update on a field-secured <paramref name="column"/>.</summary>
        public SecurityConfiguration FieldPermission(Guid principal, string entity, string column, bool read = true, bool update = false)
        {
            (Guid, string, string) key = (principal, Norm(entity), Norm(column));
            if (read)
            {
                _fieldRead.Add(key);
            }

            if (update)
            {
                _fieldUpdate.Add(key);
            }

            return this;
        }

        private static string Norm(string value) => value?.ToLowerInvariant() ?? string.Empty;

        /// <summary>Adds <paramref name="userId"/> to <paramref name="teamId"/>; the user inherits the team's roles.</summary>
        public SecurityConfiguration Member(Guid userId, Guid teamId)
        {
            if (!_memberships.TryGetValue(userId, out HashSet<Guid>? teams))
            {
                teams = new HashSet<Guid>();
                _memberships[userId] = teams;
            }

            teams.Add(teamId);
            return this;
        }

        internal SecurityModel Build(SharingTable sharing, AccessTeamGrants accessTeams)
        {
            // Dataverse activates only one hierarchy model org-wide; configuring both would silently share a single
            // depth and produce a hybrid the platform can't. Fail fast rather than model something unreal.
            if (_managerHierarchy && _positionHierarchy)
            {
                throw new InvalidOperationException(
                    "Configure either ManagerHierarchy or PositionHierarchy, not both — Dataverse activates only one hierarchy model at a time.");
            }

            // Flatten each role's grants once; resolve role references eagerly so an unknown role name fails fast.
            var roles = _roles.ToDictionary(r => r.Key, r => r.Value.Grants, StringComparer.OrdinalIgnoreCase);

            void ValidateRoles(Principal principal, string kind)
            {
                foreach (string role in principal.Roles)
                {
                    if (!roles.ContainsKey(role))
                    {
                        throw new InvalidOperationException(
                            $"{kind} {principal.Id} references role '{role}', which is not defined. Declare it with Role(\"{role}\", ...).");
                    }
                }
            }

            foreach (Principal user in _users.Values)
            {
                ValidateRoles(user, "User");
            }

            foreach (Principal team in _teams.Values)
            {
                ValidateRoles(team, "Team");
            }

            // A membership that points at an undeclared team would inherit nothing silently, so fail fast — the
            // same way an undefined role name does.
            foreach (KeyValuePair<Guid, HashSet<Guid>> membership in _memberships)
            {
                foreach (Guid teamId in membership.Value)
                {
                    if (!_teams.ContainsKey(teamId))
                    {
                        throw new InvalidOperationException(
                            $"User {membership.Key} is a member of team {teamId}, which is not defined. Declare it with Team({teamId}, ...).");
                    }
                }
            }

            return new SecurityModel(_businessUnitParents, roles, _users, _teams, _memberships, sharing, accessTeams, _roleIds, _roleTemplateIds, _managers, _positions, _positionParents, _hierarchyDepth, _securedColumns, _fieldRead, _fieldUpdate);
        }
    }

    /// <summary>A registered user or team: its id, business unit and the names of the roles it holds.</summary>
    internal readonly struct Principal
    {
        public Principal(Guid id, Guid businessUnit, IReadOnlyList<string> roles, TeamPrivilegeInheritance inheritance = TeamPrivilegeInheritance.MemberAndTeam)
        {
            Id = id;
            BusinessUnit = businessUnit;
            Roles = roles;
            Inheritance = inheritance;
        }

        public Guid Id { get; }

        public Guid BusinessUnit { get; }

        public IReadOnlyList<string> Roles { get; }

        /// <summary>For teams: how members inherit its roles. Ignored for users.</summary>
        public TeamPrivilegeInheritance Inheritance { get; }
    }

    /// <summary>
    /// The resolved, immutable access model used by <c>InMemoryOrganizationService</c> to decide whether the
    /// calling user holds a privilege over a record. Built from <see cref="SecurityConfiguration"/>.
    /// </summary>
    internal sealed class SecurityModel
    {
        // 0x80040220 PrincipalPrivilegeDenied — the platform's fault code when a caller lacks a privilege.
        // Real plugins (and Vela's own RetryPolicy) branch on this exact ErrorCode.
        private const int PrincipalPrivilegeDenied = unchecked((int)0x80040220);

        // 0x80048306 — the platform's fault code when the caller holds the privilege on the table but the record is
        // out of its reach (depth, owner, no share). Verified live for a Retrieve by id.
        private const int RecordAccessDenied = unchecked((int)0x80048306);

        // 0x80040299 TargetPrincipalLacksReadPrivilege — a record's new owner holds no Read on the table (verified live
        // on Create, Update of ownerid and Assign). 0x80042f0a TeamNotAssignedRoles — the owner is a team with no role
        // at all, raised on Update/Assign (Create still raises 0x80040299).
        private const int TargetPrincipalLacksReadPrivilege = unchecked((int)0x80040299);
        private const int TeamNotAssignedRoles = unchecked((int)0x80042f0a);

        private const string SystemUser = "systemuser";
        private const string Team = "team";

        private readonly Dictionary<Guid, Guid> _businessUnitParents;
        private readonly Dictionary<string, Dictionary<(string Entity, Privilege Privilege), AccessLevel>> _roles;
        private readonly Dictionary<Guid, Principal> _users;
        private readonly Dictionary<Guid, Principal> _teams;
        private readonly Dictionary<Guid, HashSet<Guid>> _memberships;
        private readonly SharingTable _sharing;
        private readonly AccessTeamGrants _accessTeams;
        private readonly Dictionary<string, Guid> _roleIds;
        private readonly Dictionary<string, Guid> _roleTemplateIds;
        private readonly Dictionary<Guid, Guid> _managers;
        private readonly Dictionary<Guid, Guid> _positions;
        private readonly Dictionary<Guid, Guid> _positionParents;
        private readonly int _hierarchyDepth;
        private readonly HashSet<(string Entity, string Column)> _securedColumns;
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldRead;
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldUpdate;

        // The privileges that can also be granted per-record via sharing (PoA). Create is not shareable.
        private static readonly Privilege[] ShareablePrivileges =
        {
            Privilege.Read, Privilege.Write, Privilege.Delete,
            Privilege.Append, Privilege.AppendTo, Privilege.Assign, Privilege.Share,
        };

        internal SecurityModel(
            Dictionary<Guid, Guid> businessUnitParents,
            Dictionary<string, Dictionary<(string Entity, Privilege Privilege), AccessLevel>> roles,
            Dictionary<Guid, Principal> users,
            Dictionary<Guid, Principal> teams,
            Dictionary<Guid, HashSet<Guid>> memberships,
            SharingTable sharing,
            AccessTeamGrants accessTeams,
            Dictionary<string, Guid> roleIds,
            Dictionary<string, Guid> roleTemplateIds,
            Dictionary<Guid, Guid> managers,
            Dictionary<Guid, Guid> positions,
            Dictionary<Guid, Guid> positionParents,
            int hierarchyDepth,
            HashSet<(string Entity, string Column)> securedColumns,
            HashSet<(Guid Principal, string Entity, string Column)> fieldRead,
            HashSet<(Guid Principal, string Entity, string Column)> fieldUpdate)
        {
            _businessUnitParents = businessUnitParents;
            _roles = roles;
            _users = users;
            _teams = teams;
            _memberships = memberships;
            _sharing = sharing;
            _accessTeams = accessTeams;
            _roleIds = roleIds;
            _roleTemplateIds = roleTemplateIds;
            _managers = managers;
            _positions = positions;
            _positionParents = positionParents;
            _hierarchyDepth = hierarchyDepth;
            _securedColumns = securedColumns;
            _fieldRead = fieldRead;
            _fieldUpdate = fieldUpdate;
        }

        /// <summary>Whether a column is field-secured (hidden/blocked unless a field permission grants it).</summary>
        public bool IsSecuredColumn(string entity, string column) =>
            _securedColumns.Count > 0 && _securedColumns.Contains((entity.ToLowerInvariant(), column.ToLowerInvariant()));

        /// <summary>Whether the user may read a field-secured column (directly or via a team).</summary>
        public bool CanReadColumn(Guid userId, string entity, string column) => FieldAllowed(_fieldRead, userId, entity, column);

        /// <summary>Whether the user may write a field-secured column (directly or via a team).</summary>
        public bool CanWriteColumn(Guid userId, string entity, string column) => FieldAllowed(_fieldUpdate, userId, entity, column);

        private bool FieldAllowed(HashSet<(Guid Principal, string Entity, string Column)> permissions, Guid userId, string entity, string column)
        {
            string e = entity.ToLowerInvariant();
            string c = column.ToLowerInvariant();
            if (permissions.Contains((userId, e, c)))
            {
                return true;
            }

            if (_memberships.TryGetValue(userId, out HashSet<Guid>? teams))
            {
                foreach (Guid teamId in teams)
                {
                    if (permissions.Contains((teamId, e, c)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The security-membership intersect rows to seed into the store so plugins can query role membership like
        /// the platform: <c>role</c> (one copy per business unit), <c>systemuserroles</c> (a user's direct roles),
        /// <c>teamroles</c> (a team's roles) and <c>teammembership</c> (a user's teams).
        /// </summary>
        /// <remarks>
        /// Dataverse keeps one copy of a security role per business unit. The root business unit's copy carries the
        /// role's root id (<c>roleid == parentrootroleid</c>); every other copy has its own id and points at the root
        /// through <c>parentrootroleid</c>. <c>systemuserroles.roleid</c> / <c>teamroles.roleid</c> hold the copy of
        /// the principal's business unit. The declared role id (a generated <c>Roles.XId</c>) is the root id, so the
        /// root copy keeps it and the other copies get an id derived from (root id, business unit), stable across
        /// runs. A system role declared with a template id carries it as <c>roletemplateid</c> on every copy; a custom
        /// role has none. When no single root business unit can be identified (no declared hierarchy and principals spread over
        /// several unrelated business units), every principal is treated as being in the root and gets the root id.
        /// Privilege evaluation is unaffected: it reads the declared roles, not these rows.
        /// </remarks>
        public IEnumerable<Entity> MaterializeMembershipTables()
        {
            Guid? root = RootBusinessUnit();
            List<Guid> copies = root.HasValue ? NonRootBusinessUnits(root.Value) : new List<Guid>();

            foreach (KeyValuePair<string, Guid> role in _roleIds)
            {
                EntityReference? template = _roleTemplateIds.TryGetValue(role.Key, out Guid templateId)
                    ? new EntityReference("roletemplate", templateId)
                    : null;
                Entity rootCopy = new Entity("role", role.Value)
                {
                    ["roleid"] = role.Value,
                    ["name"] = role.Key,
                    ["parentrootroleid"] = new EntityReference("role", role.Value),
                };
                if (root.HasValue)
                {
                    rootCopy["businessunitid"] = new EntityReference("businessunit", root.Value);
                }

                if (template != null)
                {
                    rootCopy["roletemplateid"] = template;
                }

                yield return rootCopy;

                foreach (Guid businessUnit in copies)
                {
                    Guid copyId = RoleCopyId(role.Value, businessUnit);
                    Entity copy = new Entity("role", copyId)
                    {
                        ["roleid"] = copyId,
                        ["name"] = role.Key,
                        ["parentrootroleid"] = new EntityReference("role", role.Value),
                        ["businessunitid"] = new EntityReference("businessunit", businessUnit),
                    };
                    if (template != null)
                    {
                        copy["roletemplateid"] = new EntityReference(template.LogicalName, template.Id);
                    }

                    yield return copy;
                }
            }

            foreach (Principal user in _users.Values)
            {
                foreach (Guid roleId in RoleIds(user.Roles))
                {
                    yield return Intersect("systemuserroles", "systemuserid", user.Id, "roleid", CopyFor(roleId, user.BusinessUnit, root));
                }
            }

            foreach (Principal team in _teams.Values)
            {
                foreach (Guid roleId in RoleIds(team.Roles))
                {
                    yield return Intersect("teamroles", "teamid", team.Id, "roleid", CopyFor(roleId, team.BusinessUnit, root));
                }
            }

            foreach (KeyValuePair<Guid, HashSet<Guid>> membership in _memberships)
            {
                foreach (Guid teamId in membership.Value)
                {
                    yield return Intersect("teammembership", "systemuserid", membership.Key, "teamid", teamId);
                }
            }
        }

        // The id of a role's copy in a business unit: the root id in the root BU (or when the root is unknown, or the
        // principal has no BU), otherwise a deterministic id derived from (root id, BU).
        private static Guid CopyFor(Guid rootRoleId, Guid businessUnit, Guid? root) =>
            !root.HasValue || businessUnit == Guid.Empty || businessUnit == root.Value
                ? rootRoleId
                : RoleCopyId(rootRoleId, businessUnit);

        private static Guid RoleCopyId(Guid rootRoleId, Guid businessUnit) =>
            DeterministicGuid.From("role", rootRoleId, businessUnit);

        // The root business unit: the single top of the declared BU hierarchy, or, when no BU is declared, the single
        // business unit the users and teams are in. Null when there is no such single top (ambiguous).
        private Guid? RootBusinessUnit()
        {
            IEnumerable<Guid> candidates = _businessUnitParents.Count > 0
                ? DeclaredBusinessUnits()
                : PrincipalBusinessUnits();

            HashSet<Guid> tops = new HashSet<Guid>();
            foreach (Guid businessUnit in candidates)
            {
                tops.Add(TopOf(businessUnit));
            }

            return tops.Count == 1 ? tops.First() : (Guid?)null;
        }

        private Guid TopOf(Guid businessUnit)
        {
            HashSet<Guid> seen = new HashSet<Guid> { businessUnit };
            while (_businessUnitParents.TryGetValue(businessUnit, out Guid parent) && parent != Guid.Empty && seen.Add(parent))
            {
                businessUnit = parent;
            }

            return businessUnit;
        }

        private IEnumerable<Guid> DeclaredBusinessUnits() =>
            _businessUnitParents.Keys.Concat(_businessUnitParents.Values).Where(id => id != Guid.Empty);

        private IEnumerable<Guid> PrincipalBusinessUnits() =>
            _users.Values.Concat(_teams.Values).Select(p => p.BusinessUnit).Where(id => id != Guid.Empty);

        // Every known business unit other than the root, in a stable order (each gets its own copy of every role).
        private List<Guid> NonRootBusinessUnits(Guid root) =>
            DeclaredBusinessUnits().Concat(PrincipalBusinessUnits())
                .Where(id => id != root)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

        private IEnumerable<Guid> RoleIds(IReadOnlyList<string> roleNames)
        {
            foreach (string name in roleNames)
            {
                if (_roleIds.TryGetValue(name, out Guid id))
                {
                    yield return id;
                }
            }
        }

        private static Entity Intersect(string table, string firstColumn, Guid firstId, string secondColumn, Guid secondId) =>
            new Entity(table, DeterministicGuid.From(table, firstId, secondId))
            {
                [firstColumn] = firstId,
                [secondColumn] = secondId,
            };

        /// <summary>
        /// The business unit a principal belongs to, or <see cref="Guid.Empty"/> if unknown. Resolves users and
        /// teams (a team principal has its own BU) so depth checks work when the "principal" is a team.
        /// </summary>
        public Guid BusinessUnitOf(Guid principalId) =>
            _users.TryGetValue(principalId, out Principal user) ? user.BusinessUnit
            : _teams.TryGetValue(principalId, out Principal team) ? team.BusinessUnit
            : Guid.Empty;

        /// <summary>
        /// The business unit of an owning principal (user or team), or <see cref="Guid.Empty"/> if unknown. Used to
        /// keep <c>owningbusinessunit</c> aligned with the owner after a record is (re)assigned.
        /// </summary>
        public Guid BusinessUnitOfPrincipal(EntityReference principal)
        {
            if (principal == null)
            {
                return Guid.Empty;
            }

            if (string.Equals(principal.LogicalName, Team, StringComparison.OrdinalIgnoreCase))
            {
                return _teams.TryGetValue(principal.Id, out Principal team) ? team.BusinessUnit : Guid.Empty;
            }

            return BusinessUnitOf(principal.Id);
        }

        /// <summary>
        /// Throws <see cref="FaultException{OrganizationServiceFault}"/> (PrincipalPrivilegeDenied) when
        /// <paramref name="userId"/> may not perform <paramref name="privilege"/> on <paramref name="record"/>.
        /// </summary>
        public void Demand(Guid userId, string entity, Privilege privilege, Entity? record)
        {
            if (!IsGranted(userId, entity, privilege, record))
            {
                throw PrivilegeDenied(userId, privilege, entity);
            }
        }

        /// <summary>Builds the PrincipalPrivilegeDenied fault, so callers that add extra access checks can throw it.</summary>
        public FaultException<OrganizationServiceFault> PrivilegeDenied(Guid userId, Privilege privilege, string entity) =>
            Fault(PrincipalPrivilegeDenied, $"Principal user (Id={userId}) is missing prv{privilege}{entity} privilege on entity '{entity}'.");

        /// <summary>
        /// Builds the fault for a record out of the caller's reach although it holds the privilege on the table
        /// (0x80048306), as a Retrieve by id raises.
        /// </summary>
        public FaultException<OrganizationServiceFault> RecordDenied(Guid userId, Privilege privilege, string entity, Guid recordId) =>
            Fault(RecordAccessDenied, $"Principal user (Id={userId}) does not have {ToAccessRight(privilege)} right(s) for record with Id = {recordId} of entity {entity}.");

        private static FaultException<OrganizationServiceFault> Fault(int errorCode, string message)
        {
            OrganizationServiceFault fault = new OrganizationServiceFault
            {
                ErrorCode = errorCode,
                Message = message,
            };

            return new FaultException<OrganizationServiceFault>(fault, new FaultReason(message));
        }

        /// <summary>
        /// The fault the platform raises when <paramref name="owner"/> may not own a record of <paramref name="entity"/>
        /// because it holds no Read on the table, or null when it may (or when the model does not know the principal,
        /// so arbitrary owner ids stay unchecked). A user qualifies through its own roles or a team's, a team through
        /// its own roles whatever its inheritance mode (verified live), at any depth.
        /// </summary>
        public FaultException<OrganizationServiceFault>? OwnerReadDenied(EntityReference owner, string entity, bool onCreate)
        {
            string table = entity.ToLowerInvariant();
            bool isTeam = string.Equals(owner.LogicalName, Team, StringComparison.OrdinalIgnoreCase);
            bool canRead;
            if (isTeam)
            {
                if (!_teams.TryGetValue(owner.Id, out Principal team))
                {
                    return null;
                }

                canRead = LevelsFor(team.Roles, table, Privilege.Read).Any();
                if (!canRead && team.Roles.Count == 0 && !onCreate)
                {
                    return Fault(TeamNotAssignedRoles, OwnerReadMessage(owner.Id, Team, entity));
                }
            }
            else
            {
                if (!_users.ContainsKey(owner.Id))
                {
                    return null;
                }

                // Learn says a user whose Read comes only from a "team privileges only" team needs it at Organization
                // level; that is unverified live, so a user owner takes Read at any level from any of its roles.
                canRead = HoldsPrivilege(owner.Id, table, Privilege.Read);
            }

            if (canRead)
            {
                return null;
            }

            string message = OwnerReadMessage(owner.Id, isTeam ? Team : "user", entity);
            return Fault(TargetPrincipalLacksReadPrivilege, onCreate ? "Read Privilege Check For Owner failed with exception: " + message : message);
        }

        private static string OwnerReadMessage(Guid ownerId, string kind, string entity) =>
            $"Principal {kind} (Id={ownerId}) is missing prvRead{entity} privilege on entity '{entity}'. " +
            "Consider adding missed privilege to one of the principal (user/team) roles.";

        /// <summary>
        /// Whether <paramref name="userId"/> holds <paramref name="privilege"/> on <paramref name="entity"/> at any
        /// depth, through its own roles or a team's, whatever the record.
        /// </summary>
        public bool HoldsPrivilege(Guid userId, string entity, Privilege privilege) =>
            GrantedLevels(userId, entity.ToLowerInvariant(), privilege).Any();

        /// <summary>
        /// Whether <paramref name="userId"/> holds <paramref name="privilege"/> on <paramref name="entity"/> that
        /// covers <paramref name="record"/>. A null record (e.g. Create) needs only a non-None grant.
        /// </summary>
        public bool IsGranted(Guid userId, string entity, Privilege privilege, Entity? record)
        {
            Guid userBu = BusinessUnitOf(userId);
            string table = entity.ToLowerInvariant();

            // The user's effective grants = its own role grants plus those of every team it belongs to.
            foreach ((AccessLevel level, Guid? restrictToTeamOwned) in GrantedLevels(userId, table, privilege))
            {
                if (restrictToTeamOwned is Guid teamId)
                {
                    // A Basic-level grant from a "Team privileges only" team covers only the team's own records
                    // (including a Create whose prospective owner is that team).
                    if (OwnedByTeam(record, teamId))
                    {
                        return true;
                    }
                }
                else if (Covers(level, userId, userBu, record))
                {
                    return true;
                }
            }

            // Record-level access is unioned with role access: an explicit share or an inherited grant (PoA, to the
            // user or any of its teams) or an access-team grant (to the user) also confers the privilege. Create is
            // never record-scoped. A PoA grant only counts when the user holds the privilege on the table at some
            // depth (Learn: sharing can't give rights the user's role doesn't have for that table; inherited access
            // was verified live to need at least User-level privilege on the child table too).
            AccessRights right = ToAccessRight(privilege);
            if (record != null && right != AccessRights.None)
            {
                AccessRights poa = GrantedLevels(userId, table, privilege).Any()
                    ? _sharing.RightsFor(table, record.Id, PrincipalIds(userId))
                    : AccessRights.None;
                AccessRights shared = poa | _accessTeams.RightsFor(table, record.Id, userId);
                if ((shared & right) == right)
                {
                    return true;
                }
            }

            // Hierarchy access (manager and/or position): a higher user reaches a report's records up the chain.
            if (record != null && HasHierarchyAccess(userId, table, privilege, record))
            {
                return true;
            }

            return false;
        }

        // The record-level rights the hierarchy can extend from a report onto a higher user. Delete/Assign/Share are
        // never conferred by hierarchy, so an owner is treated as holding only these on their own records for the walk.
        private const AccessRights HierarchyRights =
            AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess | AccessRights.AppendToAccess;

        // Hierarchy security: a higher user (manager or position) gets Read on any report's record within the configured
        // depth, and Write/Append/AppendTo on a DIRECT report (distance 1) only — never Delete/Assign/Share. The gate is
        // at least User-level READ on the table (per MS: "a manager must have at least the user level Read privilege to
        // see the reports' data") — once read is held the model confers the direct-report write set regardless of the
        // higher user's own write privilege. A "report" is any user with access to the record: its owning user, a member
        // of its owning team, or a user the record is shared to (PoA), directly or via a team.
        // The manager model requires the higher user to be in the report's BU or its parent; the position model does not
        // (it spans business units).
        private bool HasHierarchyAccess(Guid userId, string table, Privilege privilege, Entity record)
        {
            if (_hierarchyDepth <= 0)
            {
                return false;
            }

            bool wantsWrite = privilege == Privilege.Write || privilege == Privilege.Append || privilege == Privilege.AppendTo;
            if (privilege != Privilege.Read && !wantsWrite)
            {
                return false; // Delete/Assign/Share/Create are never conferred by the hierarchy
            }

            // Precondition (MS hierarchy security): the higher user must hold at least User-level Read on the table —
            // that is the single gate; the direct-report write set is then conferred by the model itself.
            if (!GrantedLevels(userId, table, Privilege.Read).Any())
            {
                return false;
            }

            AccessRights required = ToAccessRight(privilege);
            foreach ((Guid report, AccessRights held) in ReportsWithAccess(table, record))
            {
                if (report == userId || (held & required) != required)
                {
                    continue; // the report doesn't itself hold the requested right on this record
                }

                int distance = HierarchyDistance(report, userId);
                if (distance == 0)
                {
                    continue; // userId is not a higher manager/position of this report within depth
                }

                if (privilege == Privilege.Read || distance == 1)
                {
                    return true; // read at any depth; write-type only for a direct (distance-1) report
                }
            }

            return false;
        }

        // Every user that has direct access to the record, tagged with the rights they hold: the owning user (all
        // hierarchy rights), each member of the owning team (all), and each user a PoA share or inherited grant reaches —
        // directly or via a team it belongs to — with the granted mask. This is the set of "reports" whose access the hierarchy extends up.
        private IEnumerable<(Guid User, AccessRights Held)> ReportsWithAccess(string table, Entity record)
        {
            Dictionary<Guid, AccessRights> access = new Dictionary<Guid, AccessRights>();
            void Add(Guid user, AccessRights rights)
            {
                access[user] = (access.TryGetValue(user, out AccessRights existing) ? existing : AccessRights.None) | rights;
            }

            if (record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) is EntityReference owner)
            {
                if (string.Equals(owner.LogicalName, SystemUser, StringComparison.OrdinalIgnoreCase))
                {
                    Add(owner.Id, HierarchyRights);
                }
                else if (string.Equals(owner.LogicalName, Team, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Guid member in MembersOf(owner.Id))
                    {
                        Add(member, HierarchyRights);
                    }
                }
            }

            EntityReference target = new EntityReference(table, record.Id);
            foreach (PrincipalAccess share in _sharing.SharesOf(target).Concat(_sharing.InheritedOf(target)))
            {
                if (share.Principal == null)
                {
                    continue;
                }

                if (string.Equals(share.Principal.LogicalName, SystemUser, StringComparison.OrdinalIgnoreCase))
                {
                    Add(share.Principal.Id, share.AccessMask);
                }
                else if (string.Equals(share.Principal.LogicalName, Team, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Guid member in MembersOf(share.Principal.Id))
                    {
                        Add(member, share.AccessMask);
                    }
                }
            }

            return access.Select(entry => (entry.Key, entry.Value));
        }

        // The users that are members of a team (inverse of the user -> teams membership map).
        private IEnumerable<Guid> MembersOf(Guid teamId)
        {
            foreach (KeyValuePair<Guid, HashSet<Guid>> membership in _memberships)
            {
                if (membership.Value.Contains(teamId))
                {
                    yield return membership.Key;
                }
            }
        }

        // How many hierarchy levels separate a report from a higher user (1 = direct), via the manager chain or the
        // position tree, or 0 if the higher user is neither above the report within depth. The nearer of the two wins.
        private int HierarchyDistance(Guid report, Guid higher)
        {
            int manager = ManagerDistance(report, higher);
            int position = PositionDistance(report, higher);
            if (manager == 0)
            {
                return position;
            }

            return position == 0 ? manager : Math.Min(manager, position);
        }

        // Distance up the manager chain (report -> ... -> higher), bounded by depth, subject to the manager-model BU
        // rule: the higher user must be in the report's business unit or its parent. 0 if not reachable.
        private int ManagerDistance(Guid report, Guid higher)
        {
            if (_managers.Count == 0)
            {
                return 0;
            }

            Guid higherBu = BusinessUnitOf(higher);
            Guid reportBu = BusinessUnitOf(report);
            bool businessUnitOk = higherBu != Guid.Empty &&
                (higherBu == reportBu || (_businessUnitParents.TryGetValue(reportBu, out Guid parent) && higherBu == parent));
            if (!businessUnitOk)
            {
                return 0;
            }

            Guid current = report;
            HashSet<Guid> seen = new HashSet<Guid> { current };
            for (int level = 1; level <= _hierarchyDepth; level++)
            {
                if (!_managers.TryGetValue(current, out Guid manager) || !seen.Add(manager))
                {
                    break;
                }

                if (manager == higher)
                {
                    return level;
                }

                current = manager;
            }

            return 0;
        }

        // Distance up the position tree: the report's position must be a strict descendant of the higher user's
        // position within depth. Position hierarchy spans business units, so no BU rule applies. 0 if not reachable.
        private int PositionDistance(Guid report, Guid higher)
        {
            if (!_positions.TryGetValue(report, out Guid reportPosition) ||
                !_positions.TryGetValue(higher, out Guid higherPosition) ||
                reportPosition == higherPosition)
            {
                return 0; // no positions, or peers sharing a position (not "higher")
            }

            Guid current = reportPosition;
            HashSet<Guid> seen = new HashSet<Guid> { current };
            for (int level = 1; level <= _hierarchyDepth; level++)
            {
                if (!_positionParents.TryGetValue(current, out Guid parent) || !seen.Add(parent))
                {
                    break;
                }

                if (parent == higherPosition)
                {
                    return level;
                }

                current = parent;
            }

            return 0;
        }

        /// <summary>
        /// The effective <see cref="AccessRights"/> mask the user holds on the record — the union of role access,
        /// ownership, sharing and inherited access — used to answer RetrievePrincipalAccess.
        /// </summary>
        public AccessRights EffectiveAccess(Guid userId, string entity, Entity record)
        {
            AccessRights rights = AccessRights.None;
            foreach (Privilege privilege in ShareablePrivileges)
            {
                if (IsGranted(userId, entity, privilege, record))
                {
                    rights |= ToAccessRight(privilege);
                }
            }

            return rights;
        }

        // The user itself plus every team it belongs to — the set of principals a PoA grant can target.
        private IEnumerable<Guid> PrincipalIds(Guid userId)
        {
            yield return userId;
            if (_memberships.TryGetValue(userId, out HashSet<Guid>? teams))
            {
                foreach (Guid teamId in teams)
                {
                    yield return teamId;
                }
            }
        }

        // Maps a table privilege to the record-level access right sharing uses. Create has no shareable right.
        private static AccessRights ToAccessRight(Privilege privilege)
        {
            switch (privilege)
            {
                case Privilege.Read: return AccessRights.ReadAccess;
                case Privilege.Write: return AccessRights.WriteAccess;
                case Privilege.Delete: return AccessRights.DeleteAccess;
                case Privilege.Append: return AccessRights.AppendAccess;
                case Privilege.AppendTo: return AccessRights.AppendToAccess;
                case Privilege.Assign: return AccessRights.AssignAccess;
                case Privilege.Share: return AccessRights.ShareAccess;
                default: return AccessRights.None;
            }
        }

        // Each granted level, tagged with the team it must be scoped to (non-null only for a Basic/User-level grant
        // from a "Team privileges only" team, which then covers only that team's records).
        private IEnumerable<(AccessLevel Level, Guid? RestrictToTeamOwned)> GrantedLevels(Guid userId, string table, Privilege privilege)
        {
            if (_users.TryGetValue(userId, out Principal user))
            {
                foreach (AccessLevel level in LevelsFor(user.Roles, table, privilege))
                {
                    yield return (level, null);
                }
            }

            // When the principal is itself a team (e.g. RetrievePrincipalAccess for a team), use the team's own roles.
            if (_teams.TryGetValue(userId, out Principal selfTeam))
            {
                foreach (AccessLevel level in LevelsFor(selfTeam.Roles, table, privilege))
                {
                    yield return (level, null);
                }
            }

            if (_memberships.TryGetValue(userId, out HashSet<Guid>? teamIds))
            {
                foreach (Guid teamId in teamIds)
                {
                    if (_teams.TryGetValue(teamId, out Principal team))
                    {
                        bool teamOnly = team.Inheritance == TeamPrivilegeInheritance.TeamOnly;
                        foreach (AccessLevel level in LevelsFor(team.Roles, table, privilege))
                        {
                            yield return (level, teamOnly && level == AccessLevel.User ? teamId : (Guid?)null);
                        }
                    }
                }
            }
        }

        private IEnumerable<AccessLevel> LevelsFor(IReadOnlyList<string> roleNames, string table, Privilege privilege)
        {
            foreach (string roleName in roleNames)
            {
                if (_roles.TryGetValue(roleName, out var grants) &&
                    grants.TryGetValue((table, privilege), out AccessLevel level) &&
                    level != AccessLevel.None)
                {
                    yield return level;
                }
            }
        }

        // Coverage is NOT monotonic across levels: a user-owned record in another BU is covered by User but not by
        // BusinessUnit, so each granted level is tested independently rather than only the highest.
        private bool Covers(AccessLevel level, Guid userId, Guid userBu, Entity? record)
        {
            if (record == null)
            {
                // Create/entity-level demand: any non-None grant suffices (there is no record scope yet).
                return level != AccessLevel.None;
            }

            switch (level)
            {
                case AccessLevel.Organization:
                    return true;
                case AccessLevel.ParentChild:
                    return IsInSubtree(OwningBusinessUnit(record), userBu);
                case AccessLevel.BusinessUnit:
                    return OwningBusinessUnit(record) == userBu && userBu != Guid.Empty;
                case AccessLevel.User:
                    return OwnedBy(record, userId);
                default:
                    return false;
            }
        }

        // True when the record's owner is the user, or a team the user belongs to.
        private bool OwnedBy(Entity record, Guid userId)
        {
            if (record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) is not EntityReference owner)
            {
                return false;
            }

            if (string.Equals(owner.LogicalName, SystemUser, StringComparison.OrdinalIgnoreCase))
            {
                return owner.Id == userId;
            }

            if (string.Equals(owner.LogicalName, Team, StringComparison.OrdinalIgnoreCase))
            {
                // The principal owns the record if it IS the owning team, or is a member of it.
                return owner.Id == userId ||
                       (_memberships.TryGetValue(userId, out HashSet<Guid>? teams) && teams.Contains(owner.Id));
            }

            return false;
        }

        // True when the record is owned by the given team specifically (used by the Team-privileges-only scope).
        private static bool OwnedByTeam(Entity? record, Guid teamId) =>
            record?.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) is EntityReference owner &&
            string.Equals(owner.LogicalName, Team, StringComparison.OrdinalIgnoreCase) && owner.Id == teamId;

        private static Guid OwningBusinessUnit(Entity record) =>
            record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwningBusinessUnit)?.Id ?? Guid.Empty;

        // Walks candidate's parent chain to see whether it reaches root (inclusive). Guards against cycles.
        private bool IsInSubtree(Guid candidate, Guid root)
        {
            if (root == Guid.Empty || candidate == Guid.Empty)
            {
                return false;
            }

            HashSet<Guid> seen = new HashSet<Guid>();
            Guid current = candidate;
            while (current != Guid.Empty && seen.Add(current))
            {
                if (current == root)
                {
                    return true;
                }

                if (!_businessUnitParents.TryGetValue(current, out Guid parent))
                {
                    return false;
                }

                current = parent;
            }

            return false;
        }
    }
}
