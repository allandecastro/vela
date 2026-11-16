using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    // Record access: the effective access mask (RetrievePrincipalAccess) and the principals a share can reach.
    internal sealed partial class SecurityModel
    {
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
    }
}
