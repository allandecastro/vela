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
    /// How the members of a team inherit a role the team holds: the security role's "Member's privilege inheritance"
    /// setting (<c>role.isinherited</c>). Set it per role with <see cref="RoleBuilder.MemberPrivilegeInheritance"/>.
    /// </summary>
    public enum TeamPrivilegeInheritance
    {
        /// <summary>
        /// Direct User (Basic) access level and Team privileges: members use the role's grants as their own (the
        /// default for a new role).
        /// </summary>
        MemberAndTeam,

        /// <summary>
        /// Team privileges only: a member can use the role's <see cref="AccessLevel.User"/>-level grants, held through
        /// a team, only for records owned by that team, not for their own records. Higher levels are unaffected.
        /// </summary>
        TeamOnly,
    }

    /// <summary>
    /// Fluent builder for a named security role: a bag of (table, privilege, level) grants that can be
    /// assigned to users and teams via <see cref="SecurityConfiguration"/>.
    /// </summary>
    public sealed class RoleBuilder
    {
        private readonly ConfigurationVersion _version;

        internal RoleBuilder(ConfigurationVersion version)
        {
            _version = version;
        }

        // (entity, privilege) -> highest level granted. A role never lowers a privilege it already grants.
        internal Dictionary<(string Entity, Privilege Privilege), AccessLevel> Grants { get; } =
            new Dictionary<(string, Privilege), AccessLevel>();

        internal TeamPrivilegeInheritance Inheritance { get; private set; } = TeamPrivilegeInheritance.MemberAndTeam;

        /// <summary>
        /// Sets the role's "Member's privilege inheritance" (<c>role.isinherited</c>). With
        /// <see cref="TeamPrivilegeInheritance.TeamOnly"/>, a user who holds the role through a team can use its
        /// <see cref="AccessLevel.User"/>-level grants only for records that team owns. The default is
        /// <see cref="TeamPrivilegeInheritance.MemberAndTeam"/>. A role assigned to a user directly keeps all its
        /// grants whatever this setting.
        /// </summary>
        public RoleBuilder MemberPrivilegeInheritance(TeamPrivilegeInheritance inheritance)
        {
            Inheritance = inheritance;
            _version.Changed();
            return this;
        }

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

            _version.Changed();
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
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldCreate = new HashSet<(Guid, string, string)>();
        private readonly ConfigurationVersion _version = new ConfigurationVersion();

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

            RoleBuilder builder = new RoleBuilder(_version);
            configure(builder);
            _roles[name] = builder;
            _roleIds[name] = id;
            _roleTemplateIds.Remove(name);
            _version.Changed();
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

            RoleBuilder builder = new RoleBuilder(_version);
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
            _version.Changed();
            return this;
        }

        /// <summary>Registers a user in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration User(Guid id, Guid businessUnit, params string[] roles)
        {
            _users[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>());
            _version.Changed();
            return this;
        }

        /// <summary>Registers a team in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration Team(Guid id, Guid businessUnit, params string[] roles) =>
            Team(id, businessUnit, TeamPrivilegeInheritance.MemberAndTeam, roles);

        /// <summary>
        /// Registers a team and applies <paramref name="inheritance"/> to every role it holds, for this team only.
        /// On the platform the setting belongs to the role (<c>role.isinherited</c>): prefer
        /// <see cref="RoleBuilder.MemberPrivilegeInheritance"/>. With <see cref="TeamPrivilegeInheritance.TeamOnly"/>,
        /// members use these roles' Basic-level grants only for records owned by the team; a role declared
        /// <see cref="TeamPrivilegeInheritance.TeamOnly"/> stays so whatever is passed here.
        /// </summary>
        public SecurityConfiguration Team(Guid id, Guid businessUnit, TeamPrivilegeInheritance inheritance, params string[] roles)
        {
            _teams[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>(), inheritance);
            _version.Changed();
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

        /// <summary>
        /// Grants a principal (user or team) permissions on a field-secured <paramref name="column"/>, as a column
        /// security profile does: <paramref name="read"/> to see the value, <paramref name="update"/> to change it on
        /// an existing record, <paramref name="create"/> to set it when creating a record. The three are independent:
        /// <paramref name="update"/> does not allow setting the column on Create.
        /// </summary>
        public SecurityConfiguration FieldPermission(Guid principal, string entity, string column, bool read = true, bool update = false, bool create = false)
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

            if (create)
            {
                _fieldCreate.Add(key);
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
            _version.Changed();
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

            ValidatePrincipals();
            ValidateRoleIds();

            // A role whose own setting is "Team privileges only", plus each (team, role) a team declared TeamOnly holds.
            HashSet<string> teamOnlyRoles = new HashSet<string>(
                _roles.Where(r => r.Value.Inheritance == TeamPrivilegeInheritance.TeamOnly).Select(r => r.Key),
                StringComparer.OrdinalIgnoreCase);

            return new SecurityModel(_businessUnitParents, roles, teamOnlyRoles, _users, _teams, _memberships, sharing, accessTeams, _roleIds, _roleTemplateIds, _managers, _positions, _positionParents, _hierarchyDepth, _securedColumns, _fieldRead, _fieldUpdate, _fieldCreate, _version);
        }

        // Settings that name a principal or business unit the configuration does not declare would be ignored
        // silently (a manager link that never matches, a field permission nobody holds), so they fail fast.
        private void ValidatePrincipals()
        {
            foreach (Guid id in _users.Keys)
            {
                if (_teams.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        $"{id} is declared both as a user and as a team. A principal id identifies one record, a systemuser or a team.");
                }
            }

            // Once any business unit is declared, every user and team must be in a declared one: a typo would otherwise
            // leave the principal outside the hierarchy, and every depth check against it would fail without saying why.
            if (_businessUnitParents.Count > 0)
            {
                HashSet<Guid> declared = new HashSet<Guid>(
                    _businessUnitParents.Keys.Concat(_businessUnitParents.Values).Where(id => id != Guid.Empty));
                foreach ((Principal principal, string kind) in _users.Values.Select(u => (u, "User")).Concat(_teams.Values.Select(t => (t, "Team"))))
                {
                    if (!declared.Contains(principal.BusinessUnit))
                    {
                        throw new InvalidOperationException(
                            $"{kind} {principal.Id} is in business unit {principal.BusinessUnit}, which is not declared. Declare it with BusinessUnit({principal.BusinessUnit}, ...).");
                    }
                }
            }

            foreach (KeyValuePair<Guid, Guid> link in _managers)
            {
                DemandUser(link.Key, "Manager", "user");
                DemandUser(link.Value, "Manager", "manager");
            }

            foreach (Guid userId in _positions.Keys)
            {
                DemandUser(userId, "Position", "user");
            }

            foreach ((Guid principal, _, _) in _fieldRead.Concat(_fieldUpdate).Concat(_fieldCreate))
            {
                if (!_users.ContainsKey(principal) && !_teams.ContainsKey(principal))
                {
                    throw new InvalidOperationException(
                        $"FieldPermission names {principal}, which is neither a declared user nor a declared team. Declare it with User({principal}, ...) or Team({principal}, ...).");
                }
            }
        }

        private void DemandUser(Guid id, string setting, string role)
        {
            if (!_users.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"{setting} names {id} as the {role}, which is not a declared user. Declare it with User({id}, ...).");
            }
        }

        // Each role is one record: two names sharing a root id would materialize two role rows with the same id.
        private void ValidateRoleIds()
        {
            Dictionary<Guid, string> seen = new Dictionary<Guid, string>();
            foreach (KeyValuePair<string, Guid> role in _roleIds)
            {
                if (seen.TryGetValue(role.Value, out string? other))
                {
                    throw new InvalidOperationException(
                        $"Roles '{other}' and '{role.Key}' have the same id {role.Value}. Give each role its own id.");
                }

                seen[role.Value] = role.Key;
            }
        }
    }

    /// <summary>
    /// A counter bumped by every change to the configuration data a built <see cref="SecurityModel"/> keeps reading
    /// (users, teams, memberships, roles and their grants), so the model's memoized grant resolution is dropped when
    /// the configuration it was computed from changes.
    /// </summary>
    internal sealed class ConfigurationVersion
    {
        private int _value;

        public int Value => _value;

        public void Changed() => _value++;
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

        /// <summary>
        /// For teams: <see cref="TeamPrivilegeInheritance.TeamOnly"/> applies that setting to every role the team
        /// holds (the <c>Team(id, bu, inheritance, roles)</c> shim). Ignored for users.
        /// </summary>
        public TeamPrivilegeInheritance Inheritance { get; }
    }
}
