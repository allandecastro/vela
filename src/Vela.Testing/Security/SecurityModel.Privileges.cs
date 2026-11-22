using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    // Privilege evaluation: a user's role grants (its own and its teams'), the depth each covers, and IsGranted, which
    // unions role access with record-level access (sharing, access teams) and the hierarchy.
    internal sealed partial class SecurityModel
    {
        /// <summary>
        /// Whether <paramref name="userId"/> holds <paramref name="privilege"/> on <paramref name="entity"/> at any
        /// depth, through its own roles or a team's, whatever the record.
        /// </summary>
        public bool HoldsPrivilege(Guid userId, string entity, Privilege privilege) =>
            GrantedLevels(userId, entity.ToLowerInvariant(), privilege).Count > 0;

        /// <summary>
        /// Whether <paramref name="userId"/> holds <paramref name="privilege"/> on <paramref name="entity"/> that
        /// covers <paramref name="record"/>. A null record (e.g. Create) needs only a non-None grant.
        /// </summary>
        public bool IsGranted(Guid userId, string entity, Privilege privilege, Entity? record)
        {
            Guid userBu = BusinessUnitOf(userId);
            string table = entity.ToLowerInvariant();

            // The user's effective grants = its own role grants plus those of every team it belongs to.
            List<(AccessLevel Level, Guid? RestrictToTeamOwned)> granted = GrantedLevels(userId, table, privilege);
            foreach ((AccessLevel level, Guid? restrictToTeamOwned) in granted)
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
                AccessRights poa = granted.Count > 0
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
        // of a "Team privileges only" role held through a team, which then covers only that team's records).
        private List<(AccessLevel Level, Guid? RestrictToTeamOwned)> GrantedLevels(Guid userId, string table, Privilege privilege)
        {
            lock (_grantedLevels)
            {
                if (_grantedLevelsVersion != _configurationVersion.Value)
                {
                    _grantedLevels.Clear();
                    _grantedLevelsVersion = _configurationVersion.Value;
                }

                (Guid, string, Privilege) key = (userId, table, privilege);
                if (!_grantedLevels.TryGetValue(key, out List<(AccessLevel Level, Guid? RestrictToTeamOwned)>? levels))
                {
                    levels = ResolveGrantedLevels(userId, table, privilege).ToList();
                    _grantedLevels[key] = levels;
                }

                return levels;
            }
        }

        private IEnumerable<(AccessLevel Level, Guid? RestrictToTeamOwned)> ResolveGrantedLevels(Guid userId, string table, Privilege privilege)
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
                        bool teamOnlyTeam = team.Inheritance == TeamPrivilegeInheritance.TeamOnly;
                        foreach ((string role, AccessLevel level) in RoleLevels(team.Roles, table, privilege))
                        {
                            bool teamOnly = teamOnlyTeam || _roles[role].Inheritance == TeamPrivilegeInheritance.TeamOnly;
                            yield return (level, teamOnly && level == AccessLevel.User ? teamId : (Guid?)null);
                        }
                    }
                }
            }
        }

        private IEnumerable<AccessLevel> LevelsFor(IReadOnlyList<string> roleNames, string table, Privilege privilege) =>
            RoleLevels(roleNames, table, privilege).Select(grant => grant.Level);

        // Each role of roleNames granting the privilege on the table, with the level it grants.
        private IEnumerable<(string Role, AccessLevel Level)> RoleLevels(IReadOnlyList<string> roleNames, string table, Privilege privilege)
        {
            foreach (string roleName in roleNames)
            {
                if (_roles.TryGetValue(roleName, out RoleBuilder? role) &&
                    role.Grants.TryGetValue((table, privilege), out AccessLevel level) &&
                    level != AccessLevel.None)
                {
                    yield return (roleName, level);
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

            if (PrincipalKind.IsUser(owner))
            {
                return owner.Id == userId;
            }

            if (PrincipalKind.IsTeam(owner))
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
            PrincipalKind.IsTeam(owner) && owner.Id == teamId;
    }
}
