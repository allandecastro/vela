using System;
using System.Collections.Generic;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// The in-memory equivalent of Dataverse's <c>principalobjectaccess</c> (PoA) table: per-record grants of
    /// <see cref="AccessRights"/> to a principal (user or team), independent of security roles. Sharing is
    /// additive — a grant only ever adds access. Mutated by GrantAccess/ModifyAccess/RevokeAccess and read by
    /// <see cref="SecurityModel"/> when it unions role access with shared access.
    /// </summary>
    internal sealed class SharingTable
    {
        // (entityLogicalName, recordId) -> (principalId -> its granted rights + reference). Keyed by principal id
        // (GUIDs are unique across users/teams); the EntityReference is kept so shares can be listed back.
        private readonly Dictionary<(string Entity, Guid Record), Dictionary<Guid, PrincipalAccess>> _grants =
            new Dictionary<(string, Guid), Dictionary<Guid, PrincipalAccess>>(RecordComparer.Instance);

        /// <summary>Adds the granted rights to the principal's existing grant on the record (GrantAccess).</summary>
        public void Grant(EntityReference target, PrincipalAccess access)
        {
            Dictionary<Guid, PrincipalAccess> record = RecordGrants(target, create: true)!;
            AccessRights existing = record.TryGetValue(access.Principal.Id, out PrincipalAccess current)
                ? current.AccessMask
                : AccessRights.None;

            record[access.Principal.Id] = new PrincipalAccess
            {
                Principal = access.Principal,
                AccessMask = existing | access.AccessMask,
            };
        }

        /// <summary>Replaces the principal's rights on the record (ModifyAccess); removes the grant if None.</summary>
        public void Modify(EntityReference target, PrincipalAccess access)
        {
            if (access.AccessMask == AccessRights.None)
            {
                Revoke(target, access.Principal);
                return;
            }

            Dictionary<Guid, PrincipalAccess> record = RecordGrants(target, create: true)!;
            record[access.Principal.Id] = new PrincipalAccess { Principal = access.Principal, AccessMask = access.AccessMask };
        }

        /// <summary>Removes the principal's grant on the record entirely (RevokeAccess).</summary>
        public void Revoke(EntityReference target, EntityReference principal)
        {
            RecordGrants(target, create: false)?.Remove(principal.Id);
        }

        /// <summary>The union of rights granted to any of <paramref name="principalIds"/> on the record.</summary>
        public AccessRights RightsFor(string entityName, Guid recordId, IEnumerable<Guid> principalIds)
        {
            Dictionary<Guid, PrincipalAccess>? record = RecordGrants(entityName, recordId, create: false);
            if (record == null)
            {
                return AccessRights.None;
            }

            HashSet<Guid> principals = new HashSet<Guid>(principalIds);
            AccessRights rights = AccessRights.None;
            foreach (PrincipalAccess access in record.Values)
            {
                // A grant applies to the caller if it targets the user/one of their teams, or the whole organization.
                if (principals.Contains(access.Principal.Id) || IsOrganization(access.Principal))
                {
                    rights |= access.AccessMask;
                }
            }

            return rights;
        }

        private static bool IsOrganization(EntityReference principal) =>
            string.Equals(principal.LogicalName, "organization", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every principal the record is shared with, and their rights (RetrieveSharedPrincipalsAndAccess).</summary>
        public PrincipalAccess[] SharesOf(EntityReference target)
        {
            Dictionary<Guid, PrincipalAccess>? record = RecordGrants(target, create: false);
            return record == null ? Array.Empty<PrincipalAccess>() : new List<PrincipalAccess>(record.Values).ToArray();
        }

        private Dictionary<Guid, PrincipalAccess>? RecordGrants(EntityReference target, bool create) =>
            RecordGrants(target.LogicalName, target.Id, create);

        private Dictionary<Guid, PrincipalAccess>? RecordGrants(string entityName, Guid recordId, bool create)
        {
            var key = (entityName, recordId);
            if (!_grants.TryGetValue(key, out Dictionary<Guid, PrincipalAccess>? record) && create)
            {
                record = new Dictionary<Guid, PrincipalAccess>();
                _grants[key] = record;
            }

            return record;
        }

        // Entity names are case-insensitive across the harness; the record id is compared verbatim.
        private sealed class RecordComparer : IEqualityComparer<(string Entity, Guid Record)>
        {
            public static readonly RecordComparer Instance = new RecordComparer();

            public bool Equals((string Entity, Guid Record) x, (string Entity, Guid Record) y) =>
                x.Record == y.Record && string.Equals(x.Entity, y.Entity, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Entity, Guid Record) obj) =>
                obj.Record.GetHashCode() ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Entity ?? string.Empty);
        }
    }
}
