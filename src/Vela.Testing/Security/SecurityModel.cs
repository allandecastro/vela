using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// The resolved, immutable access model used by <c>InMemoryOrganizationService</c> to decide whether the
    /// calling user holds a privilege over a record. Built from <see cref="SecurityConfiguration"/>.
    /// </summary>
    internal sealed partial class SecurityModel
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

        private const string Team = PrincipalKind.Team;

        private readonly Dictionary<Guid, Guid> _businessUnitParents;
        private readonly Dictionary<string, Dictionary<(string Entity, Privilege Privilege), AccessLevel>> _roles;
        private readonly HashSet<string> _teamOnlyRoles;
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
        private readonly HashSet<(Guid Principal, string Entity, string Column)> _fieldCreate;
        private readonly ConfigurationVersion _configurationVersion;

        // GrantedLevels memoized per (principal, table, privilege): IsGranted runs for every record a read filters and
        // resolves the same grants up to three times per call. The result depends only on the principal's roles, its
        // teams and the roles' grants, so the memo is dropped whenever the configuration changes (its version).
        private readonly Dictionary<(Guid Principal, string Table, Privilege Privilege), List<(AccessLevel Level, Guid? RestrictToTeamOwned)>> _grantedLevels =
            new Dictionary<(Guid, string, Privilege), List<(AccessLevel, Guid?)>>();
        private int _grantedLevelsVersion;

        // The privileges that can also be granted per-record via sharing (PoA). Create is not shareable.
        private static readonly Privilege[] ShareablePrivileges =
        {
            Privilege.Read, Privilege.Write, Privilege.Delete,
            Privilege.Append, Privilege.AppendTo, Privilege.Assign, Privilege.Share,
        };

        internal SecurityModel(
            Dictionary<Guid, Guid> businessUnitParents,
            Dictionary<string, Dictionary<(string Entity, Privilege Privilege), AccessLevel>> roles,
            HashSet<string> teamOnlyRoles,
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
            HashSet<(Guid Principal, string Entity, string Column)> fieldUpdate,
            HashSet<(Guid Principal, string Entity, string Column)> fieldCreate,
            ConfigurationVersion configurationVersion)
        {
            _businessUnitParents = businessUnitParents;
            _roles = roles;
            _teamOnlyRoles = teamOnlyRoles;
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
            _fieldCreate = fieldCreate;
            _configurationVersion = configurationVersion;
        }

        // The root business unit: the single top of the declared BU hierarchy, or, when no BU is declared, the single
        // business unit the users and teams are in. Null when there is no such single top (ambiguous).
        /// <summary>The root business unit of the model, or null when there is no single top business unit.</summary>
        internal Guid? RootBusinessUnitId() => RootBusinessUnit();

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

            if (PrincipalKind.IsTeam(principal))
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
            bool isTeam = PrincipalKind.IsTeam(owner);
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

                // A Basic-level Read from a "team privileges only" team does not count (verified live: 0x80040299);
                // Business Unit level already does (Learn says Organization; Business Unit was enough live, with the
                // owner and the record in the same business unit). Read from the user's own roles counts at any level.
                canRead = GrantedLevels(owner.Id, table, Privilege.Read).Any(grant => grant.RestrictToTeamOwned == null);
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
