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

        /// <summary>Grants a single <paramref name="privilege"/> on <paramref name="entity"/> at <paramref name="level"/>.</summary>
        public RoleBuilder Grant(string entity, Privilege privilege, AccessLevel level)
        {
            if (string.IsNullOrWhiteSpace(entity))
            {
                throw new ArgumentException("Entity logical name is required.", nameof(entity));
            }

            // Entity logical names are case-insensitive across the harness, so normalize the key to match.
            var key = (entity.ToLowerInvariant(), privilege);
            if (!Grants.TryGetValue(key, out AccessLevel existing) || level > existing)
            {
                Grants[key] = level;
            }

            return this;
        }

        /// <summary>Grants several privileges on <paramref name="entity"/> at the same <paramref name="level"/>.</summary>
        public RoleBuilder Grant(string entity, AccessLevel level, params Privilege[] privileges)
        {
            foreach (Privilege privilege in privileges ?? Array.Empty<Privilege>())
            {
                Grant(entity, privilege, level);
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
        private readonly Dictionary<Guid, Guid> _managers = new Dictionary<Guid, Guid>();
        private int _hierarchyDepth;

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
        /// Defines a named role with an explicit <paramref name="id"/> (e.g. a generated <c>Roles.XId</c>), so
        /// <c>systemuserroles</c>/<c>teamroles</c> queries return that id. Without an id one is derived from the name.
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
            _hierarchyDepth = depth < 0 ? 0 : depth;
            return this;
        }

        /// <summary>Declares that <paramref name="userId"/> reports to <paramref name="managerId"/> (manager hierarchy).</summary>
        public SecurityConfiguration Manager(Guid userId, Guid managerId)
        {
            _managers[userId] = managerId;
            return this;
        }

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

            return new SecurityModel(_businessUnitParents, roles, _users, _teams, _memberships, sharing, accessTeams, _roleIds, _managers, _hierarchyDepth);
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
        private readonly Dictionary<Guid, Guid> _managers;
        private readonly int _hierarchyDepth;

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
            Dictionary<Guid, Guid> managers,
            int hierarchyDepth)
        {
            _businessUnitParents = businessUnitParents;
            _roles = roles;
            _users = users;
            _teams = teams;
            _memberships = memberships;
            _sharing = sharing;
            _accessTeams = accessTeams;
            _roleIds = roleIds;
            _managers = managers;
            _hierarchyDepth = hierarchyDepth;
        }

        /// <summary>
        /// The security-membership intersect rows to seed into the store so plugins can query role membership like
        /// the platform: <c>role</c> (id + name), <c>systemuserroles</c> (a user's direct roles), <c>teamroles</c>
        /// (a team's roles) and <c>teammembership</c> (a user's teams).
        /// </summary>
        public IEnumerable<Entity> MaterializeMembershipTables()
        {
            foreach (KeyValuePair<string, Guid> role in _roleIds)
            {
                yield return new Entity("role", role.Value) { ["roleid"] = role.Value, ["name"] = role.Key };
            }

            foreach (Principal user in _users.Values)
            {
                foreach (Guid roleId in RoleIds(user.Roles))
                {
                    yield return Intersect("systemuserroles", "systemuserid", user.Id, "roleid", roleId);
                }
            }

            foreach (Principal team in _teams.Values)
            {
                foreach (Guid roleId in RoleIds(team.Roles))
                {
                    yield return Intersect("teamroles", "teamid", team.Id, "roleid", roleId);
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

        /// <summary>The business unit the user belongs to, or <see cref="Guid.Empty"/> if the user is unknown.</summary>
        public Guid BusinessUnitOf(Guid userId) =>
            _users.TryGetValue(userId, out Principal user) ? user.BusinessUnit : Guid.Empty;

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
        public FaultException<OrganizationServiceFault> PrivilegeDenied(Guid userId, Privilege privilege, string entity)
        {
            string message = $"Principal user ({userId}) is missing the {privilege} privilege on '{entity}'.";
            OrganizationServiceFault fault = new OrganizationServiceFault
            {
                ErrorCode = PrincipalPrivilegeDenied,
                Message = message,
            };

            return new FaultException<OrganizationServiceFault>(fault, new FaultReason(message));
        }

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
                    // A Basic-level grant from a "Team privileges only" team covers only the team's own records.
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

            // Shared access is unioned with role access: an explicit record share (PoA, to the user or any of its
            // teams) or an access-team grant (to the user) also confers the privilege. Create is never record-scoped.
            AccessRights right = ToAccessRight(privilege);
            if (record != null && right != AccessRights.None)
            {
                AccessRights shared = _sharing.RightsFor(table, record.Id, PrincipalIds(userId))
                    | _accessTeams.RightsFor(table, record.Id, userId);
                if ((shared & right) == right)
                {
                    return true;
                }
            }

            // Manager-hierarchy access: a manager reaches a report's records up the reporting chain.
            if (record != null && HasHierarchyAccess(userId, table, privilege, record))
            {
                return true;
            }

            return false;
        }

        // Manager hierarchy: a manager gets Read on any report within the configured depth, and Write/Append/AppendTo
        // on a direct report (level 1) only — never Delete/Assign/Share. It only extends a manager who ALREADY holds
        // the privilege at BusinessUnit/ParentChild level through a role, and who is in the report's business unit or
        // its parent. The record's owning user is the report; we walk up the reporting chain from it.
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

            // Precondition: the manager must have a role granting this privilege at BusinessUnit or ParentChild depth
            // (a BU/Deep grant is never team-restricted, so the restriction tag is irrelevant here).
            if (!GrantedLevels(userId, table, privilege).Any(l => l.Level == AccessLevel.BusinessUnit || l.Level == AccessLevel.ParentChild))
            {
                return false;
            }

            if (record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) is not EntityReference owner ||
                !string.Equals(owner.LogicalName, SystemUser, StringComparison.OrdinalIgnoreCase))
            {
                return false; // user-owned records only
            }

            Guid managerBu = BusinessUnitOf(userId);
            Guid reportBu = OwningBusinessUnit(record);
            bool businessUnitOk = managerBu != Guid.Empty &&
                (managerBu == reportBu || (_businessUnitParents.TryGetValue(reportBu, out Guid parent) && managerBu == parent));
            if (!businessUnitOk)
            {
                return false;
            }

            Guid current = owner.Id;
            HashSet<Guid> seen = new HashSet<Guid> { current };
            for (int level = 1; level <= _hierarchyDepth; level++)
            {
                if (!_managers.TryGetValue(current, out Guid manager) || !seen.Add(manager))
                {
                    break;
                }

                if (manager == userId)
                {
                    return privilege == Privilege.Read || level == 1; // write-type only for a direct report
                }

                current = manager;
            }

            return false;
        }

        /// <summary>
        /// The effective <see cref="AccessRights"/> mask the user holds on the record — the union of role access,
        /// ownership and sharing — used to answer RetrievePrincipalAccess.
        /// </summary>
        public AccessRights EffectiveAccess(Guid userId, string entity, Entity record, Func<Privilege, bool>? inherited = null)
        {
            AccessRights rights = AccessRights.None;
            foreach (Privilege privilege in ShareablePrivileges)
            {
                if (IsGranted(userId, entity, privilege, record) || (inherited?.Invoke(privilege) ?? false))
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
                return _memberships.TryGetValue(userId, out HashSet<Guid>? teams) && teams.Contains(owner.Id);
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
