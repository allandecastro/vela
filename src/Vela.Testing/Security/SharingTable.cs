using System;
using System.Collections.Generic;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// The in-memory equivalent of Dataverse's <c>principalobjectaccess</c> (PoA) table: per-record grants of
    /// <see cref="AccessRights"/> to a principal (user or team), independent of security roles. Sharing is
    /// additive — a grant only ever adds access. Two kinds of grant are kept apart, like the PoA row's
    /// <c>accessrightsmask</c> and <c>inheritedaccessrightsmask</c>: <b>explicit</b> shares (GrantAccess/ModifyAccess/
    /// RevokeAccess, listed by RetrieveSharedPrincipalsAndAccess) and <b>inherited</b> grants a child record receives
    /// from its parental parent (Reparent and Share cascades). Both are read by <see cref="SecurityModel"/> when it
    /// unions role access with record-level access.
    /// </summary>
    internal sealed class SharingTable
    {
        private readonly GrantSet _explicit = new GrantSet();
        private readonly GrantSet _inherited = new GrantSet();

        /// <summary>Adds the granted rights to the principal's existing explicit share on the record (GrantAccess).</summary>
        public void Grant(EntityReference target, PrincipalAccess access) => _explicit.Grant(target, access);

        /// <summary>Replaces the principal's explicit rights on the record (ModifyAccess); removes the share if None.</summary>
        public void Modify(EntityReference target, PrincipalAccess access) => _explicit.Modify(target, access);

        /// <summary>Removes the principal's explicit share on the record entirely (RevokeAccess).</summary>
        public void Revoke(EntityReference target, EntityReference principal) => _explicit.Revoke(target, principal);

        /// <summary>Adds rights to the principal's inherited grant on the child record (Reparent/Share cascade).</summary>
        public void GrantInherited(EntityReference target, PrincipalAccess access) => _inherited.Grant(target, access);

        /// <summary>Replaces the principal's inherited rights on the child record (cascaded ModifyAccess).</summary>
        public void ModifyInherited(EntityReference target, PrincipalAccess access) => _inherited.Modify(target, access);

        /// <summary>Removes the principal's inherited grant on the child record (Unshare cascade).</summary>
        public void RevokeInherited(EntityReference target, EntityReference principal) => _inherited.Revoke(target, principal);

        /// <summary>Removes every inherited grant on the record (its parent changed, so they are re-derived).</summary>
        public void ClearInherited(EntityReference target) => _inherited.Clear(target);

        /// <summary>
        /// The union of explicit and inherited rights granted to any of <paramref name="principalIds"/> (or to the
        /// whole organization) on the record.
        /// </summary>
        public AccessRights RightsFor(string entityName, Guid recordId, IEnumerable<Guid> principalIds)
        {
            HashSet<Guid> principals = new HashSet<Guid>(principalIds);
            return _explicit.RightsFor(entityName, recordId, principals) | _inherited.RightsFor(entityName, recordId, principals);
        }

        /// <summary>Every principal the record is explicitly shared with, and their rights (RetrieveSharedPrincipalsAndAccess).</summary>
        public PrincipalAccess[] SharesOf(EntityReference target) => _explicit.Of(target);

        /// <summary>Every principal holding an inherited grant on the record, and its rights.</summary>
        public PrincipalAccess[] InheritedOf(EntityReference target) => _inherited.Of(target);

        /// <summary>Deep-copies the whole table so a transaction/operation can roll back to this point.</summary>
        internal SharingTable Clone()
        {
            SharingTable copy = new SharingTable();
            copy.CopyFrom(this);
            return copy;
        }

        /// <summary>Replaces this table's contents with a deep copy of <paramref name="source"/> (rollback).</summary>
        internal void CopyFrom(SharingTable source)
        {
            _explicit.CopyFrom(source._explicit);
            _inherited.CopyFrom(source._inherited);
        }

        // One kind of grant: (entityLogicalName, recordId) -> (principalId -> its rights + reference). Keyed by
        // principal id (GUIDs are unique across users/teams); the EntityReference is kept so grants can be listed back.
        private sealed class GrantSet
        {
            private readonly Dictionary<(string Entity, Guid Record), Dictionary<Guid, PrincipalAccess>> _grants =
                new Dictionary<(string, Guid), Dictionary<Guid, PrincipalAccess>>(RecordComparer.Instance);

            public void Grant(EntityReference target, PrincipalAccess access)
            {
                Dictionary<Guid, PrincipalAccess> record = RecordGrants(target.LogicalName, target.Id, create: true)!;
                AccessRights existing = record.TryGetValue(access.Principal.Id, out PrincipalAccess current)
                    ? current.AccessMask
                    : AccessRights.None;

                record[access.Principal.Id] = new PrincipalAccess
                {
                    Principal = access.Principal,
                    AccessMask = existing | access.AccessMask,
                };
            }

            public void Modify(EntityReference target, PrincipalAccess access)
            {
                if (access.AccessMask == AccessRights.None)
                {
                    Revoke(target, access.Principal);
                    return;
                }

                Dictionary<Guid, PrincipalAccess> record = RecordGrants(target.LogicalName, target.Id, create: true)!;
                record[access.Principal.Id] = new PrincipalAccess { Principal = access.Principal, AccessMask = access.AccessMask };
            }

            public void Revoke(EntityReference target, EntityReference principal) =>
                RecordGrants(target.LogicalName, target.Id, create: false)?.Remove(principal.Id);

            public void Clear(EntityReference target) => _grants.Remove((target.LogicalName, target.Id));

            public AccessRights RightsFor(string entityName, Guid recordId, HashSet<Guid> principals)
            {
                Dictionary<Guid, PrincipalAccess>? record = RecordGrants(entityName, recordId, create: false);
                if (record == null)
                {
                    return AccessRights.None;
                }

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

            public PrincipalAccess[] Of(EntityReference target)
            {
                Dictionary<Guid, PrincipalAccess>? record = RecordGrants(target.LogicalName, target.Id, create: false);
                return record == null ? Array.Empty<PrincipalAccess>() : new List<PrincipalAccess>(record.Values).ToArray();
            }

            public void CopyFrom(GrantSet source)
            {
                _grants.Clear();
                foreach (KeyValuePair<(string Entity, Guid Record), Dictionary<Guid, PrincipalAccess>> record in source._grants)
                {
                    Dictionary<Guid, PrincipalAccess> inner = new Dictionary<Guid, PrincipalAccess>();
                    foreach (KeyValuePair<Guid, PrincipalAccess> grant in record.Value)
                    {
                        inner[grant.Key] = new PrincipalAccess { Principal = grant.Value.Principal, AccessMask = grant.Value.AccessMask };
                    }

                    _grants[record.Key] = inner;
                }
            }

            private static bool IsOrganization(EntityReference principal) =>
                string.Equals(principal.LogicalName, "organization", StringComparison.OrdinalIgnoreCase);

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
