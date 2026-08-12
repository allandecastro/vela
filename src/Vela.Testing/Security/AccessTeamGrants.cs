using System;
using System.Collections.Generic;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// Access-team grants: a user added to a record's access team (from a team template) gets the template's
    /// <see cref="AccessRights"/> on that record. Kept per (record, user, template) — distinct from explicit
    /// sharing (PoA) — so removing a user from one access team leaves any other team's grant intact. Unioned into
    /// <see cref="SecurityModel"/>'s access decision alongside role and shared access.
    /// </summary>
    internal sealed class AccessTeamGrants
    {
        // (entityLogicalName lower-cased, recordId) -> ((userId, templateId) -> rights).
        private readonly Dictionary<(string Entity, Guid Record), Dictionary<(Guid User, Guid Template), AccessRights>> _grants =
            new Dictionary<(string, Guid), Dictionary<(Guid, Guid), AccessRights>>();

        /// <summary>Adds the template's rights for the user on the record (AddUserToRecordTeam).</summary>
        public void Add(EntityReference record, Guid userId, Guid templateId, AccessRights rights)
        {
            var key = Key(record.LogicalName, record.Id);
            if (!_grants.TryGetValue(key, out Dictionary<(Guid, Guid), AccessRights>? forRecord))
            {
                forRecord = new Dictionary<(Guid, Guid), AccessRights>();
                _grants[key] = forRecord;
            }

            forRecord[(userId, templateId)] = rights;
        }

        /// <summary>Removes the user's grant from one access team on the record (RemoveUserFromRecordTeam).</summary>
        public void Remove(EntityReference record, Guid userId, Guid templateId)
        {
            if (_grants.TryGetValue(Key(record.LogicalName, record.Id), out Dictionary<(Guid, Guid), AccessRights>? forRecord))
            {
                forRecord.Remove((userId, templateId));
            }
        }

        /// <summary>The union of rights the user holds on the record across every access team they are in.</summary>
        public AccessRights RightsFor(string entityName, Guid recordId, Guid userId)
        {
            if (!_grants.TryGetValue(Key(entityName, recordId), out Dictionary<(Guid, Guid), AccessRights>? forRecord))
            {
                return AccessRights.None;
            }

            AccessRights rights = AccessRights.None;
            foreach (KeyValuePair<(Guid User, Guid Template), AccessRights> grant in forRecord)
            {
                if (grant.Key.User == userId)
                {
                    rights |= grant.Value;
                }
            }

            return rights;
        }

        private static (string, Guid) Key(string entityName, Guid recordId) => (entityName.ToLowerInvariant(), recordId);
    }
}
