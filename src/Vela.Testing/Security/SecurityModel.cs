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
        public SecurityConfiguration Role(string name, Action<RoleBuilder> configure)
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
            return this;
        }

        /// <summary>Registers a user in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration User(Guid id, Guid businessUnit, params string[] roles)
        {
            _users[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>());
            return this;
        }

        /// <summary>Registers a team in <paramref name="businessUnit"/> holding the named <paramref name="roles"/>.</summary>
        public SecurityConfiguration Team(Guid id, Guid businessUnit, params string[] roles)
        {
            _teams[id] = new Principal(id, businessUnit, roles ?? Array.Empty<string>());
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

        internal SecurityModel Build(SharingTable sharing)
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

            return new SecurityModel(_businessUnitParents, roles, _users, _teams, _memberships, sharing);
        }
    }

    /// <summary>A registered user or team: its id, business unit and the names of the roles it holds.</summary>
    internal readonly struct Principal
    {
        public Principal(Guid id, Guid businessUnit, IReadOnlyList<string> roles)
        {
            Id = id;
            BusinessUnit = businessUnit;
            Roles = roles;
        }

        public Guid Id { get; }

        public Guid BusinessUnit { get; }

        public IReadOnlyList<string> Roles { get; }
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
            SharingTable sharing)
        {
            _businessUnitParents = businessUnitParents;
            _roles = roles;
            _users = users;
            _teams = teams;
            _memberships = memberships;
            _sharing = sharing;
        }

        /// <summary>The business unit the user belongs to, or <see cref="Guid.Empty"/> if the user is unknown.</summary>
        public Guid BusinessUnitOf(Guid userId) =>
            _users.TryGetValue(userId, out Principal user) ? user.BusinessUnit : Guid.Empty;

        /// <summary>
        /// Throws <see cref="FaultException{OrganizationServiceFault}"/> (PrincipalPrivilegeDenied) when
        /// <paramref name="userId"/> may not perform <paramref name="privilege"/> on <paramref name="record"/>.
        /// </summary>
        public void Demand(Guid userId, string entity, Privilege privilege, Entity? record)
        {
            if (IsGranted(userId, entity, privilege, record))
            {
                return;
            }

            string message = $"Principal user ({userId}) is missing the {privilege} privilege on '{entity}'.";
            OrganizationServiceFault fault = new OrganizationServiceFault
            {
                ErrorCode = PrincipalPrivilegeDenied,
                Message = message,
            };

            throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(message));
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
            foreach (AccessLevel level in GrantedLevels(userId, table, privilege))
            {
                if (Covers(level, userId, userBu, record))
                {
                    return true;
                }
            }

            // Shared access (PoA) is unioned with role access: a record-level grant to the user or any of its
            // teams also confers the privilege. Create is never shareable, so only record-scoped verbs apply.
            AccessRights right = ToAccessRight(privilege);
            if (record != null && right != AccessRights.None)
            {
                if ((_sharing.RightsFor(table, record.Id, PrincipalIds(userId)) & right) == right)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The effective <see cref="AccessRights"/> mask the user holds on the record — the union of role access,
        /// ownership and sharing — used to answer RetrievePrincipalAccess.
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

        private IEnumerable<AccessLevel> GrantedLevels(Guid userId, string table, Privilege privilege)
        {
            if (_users.TryGetValue(userId, out Principal user))
            {
                foreach (AccessLevel level in LevelsFor(user.Roles, table, privilege))
                {
                    yield return level;
                }
            }

            if (_memberships.TryGetValue(userId, out HashSet<Guid>? teamIds))
            {
                foreach (Guid teamId in teamIds)
                {
                    if (_teams.TryGetValue(teamId, out Principal team))
                    {
                        foreach (AccessLevel level in LevelsFor(team.Roles, table, privilege))
                        {
                            yield return level;
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
