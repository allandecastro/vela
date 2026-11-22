using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // Security enforcement wiring for InMemoryOrganizationService: privilege demands, field-level masking, inherited
    // access, share clamping, owner/BU stamping, relationship cascades, the read filter and the membership tables.
    // Split into its own partial so mutation testing can target the enforcement surface in isolation.
    public sealed partial class InMemoryOrganizationService
    {
        // The platform's security-membership tables plugins query to resolve roles, seeded from the security model
        // (below). role is read under the same rules as any table (prvReadRole, business-unit owned). The intersect
        // tables keep no read check: which privilege gates them on the platform has not been verified.
        private const string RoleTable = "role";

        private static readonly HashSet<string> MembershipIntersectTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "systemuserroles", "teamroles", "teammembership",
        };

        // Rebuild the role/systemuserroles/teamroles/teammembership rows from the security model, so a plugin can
        // query role membership like the platform (e.g. RetrieveMultiple over systemuserroles, or a join to role).
        private void MaterializeSecurityTables()
        {
            _store.Remove(RoleTable);
            foreach (string table in MembershipIntersectTables)
            {
                _store.Remove(table);
            }

            if (_security == null)
            {
                return;
            }

            foreach (Entity row in _security.MaterializeMembershipTables())
            {
                if (string.Equals(row.LogicalName, RoleTable, StringComparison.OrdinalIgnoreCase))
                {
                    StampRoleBusinessUnit(row);
                }

                Table(row.LogicalName)[row.Id] = row;
            }
        }

        // A role copy is owned by its business unit (businessunitid), so a Business Unit or Parent: Child read depth
        // reaches it. A copy with no businessunitid (no single root business unit) is reached at Organization depth only.
        // Applied to the materialized copies and to role rows given to Seed.
        private static void StampRoleBusinessUnit(Entity role)
        {
            if (!role.Contains(SystemFieldApplier.OwningBusinessUnit) &&
                role.GetAttributeValue<EntityReference>("businessunitid") is EntityReference businessUnit)
            {
                role[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", businessUnit.Id);
            }
        }

        // Read demand for a Retrieve by id: no Read privilege on the table at any depth fails with the privilege fault
        // (0x80040220); a privilege that does not reach this record fails with the record fault (0x80048306).
        private void DemandRecordRead(string entityName, Entity stored)
        {
            if (MembershipIntersectTables.Contains(entityName))
            {
                return;
            }

            if (Enforcing == null)
            {
                DemandStrictSecurityConfigured();
                return;
            }

            if (!_security!.HoldsPrivilege(_currentUserId, entityName, Privilege.Read))
            {
                throw _security.PrivilegeDenied(_currentUserId, Privilege.Read, entityName);
            }

            if (!CanAccess(entityName, Privilege.Read, stored))
            {
                throw _security.RecordDenied(_currentUserId, Privilege.Read, entityName, stored.Id);
            }
        }

        // A query fails as a whole when the caller holds no Read privilege on a table it reads: the root table and
        // the table of every link-entity, at any nesting level, whatever its join, columns or filters (verified live).
        // Rows the privilege does not reach are filtered by the engine instead.
        private void DemandQueryReadPrivileges(QueryExpression query)
        {
            if (Enforcing == null)
            {
                return;
            }

            DemandTableRead(query.EntityName);
            DemandLinkReads(query.Criteria);
            foreach (LinkEntity link in query.LinkEntities)
            {
                DemandLinkReads(link);
            }
        }

        private void DemandLinkReads(LinkEntity link)
        {
            DemandTableRead(link.LinkToEntityName);
            DemandLinkReads(link.LinkCriteria);
            foreach (LinkEntity child in link.LinkEntities)
            {
                DemandLinkReads(child);
            }
        }

        // A filter can carry a link-entity of its own (an any/all related-record filter).
        private void DemandLinkReads(FilterExpression? filter)
        {
            if (filter == null)
            {
                return;
            }

            if (filter.AnyAllFilterLinkEntity != null)
            {
                DemandLinkReads(filter.AnyAllFilterLinkEntity);
            }

            foreach (FilterExpression child in filter.Filters)
            {
                DemandLinkReads(child);
            }
        }

        private void DemandTableRead(string entityName)
        {
            if (!string.IsNullOrEmpty(entityName) &&
                !MembershipIntersectTables.Contains(entityName) &&
                !_security!.HoldsPrivilege(_currentUserId, entityName, Privilege.Read))
            {
                throw _security.PrivilegeDenied(_currentUserId, Privilege.Read, entityName);
            }
        }
        // owningbusinessunit tracks the record's owner: after the owner is stamped (on create, or on an ownership
        // change), realign it to the OWNER's business unit — not the acting user's — so BU/Deep depth checks are
        // correct even when a record is created for, or assigned to, a principal in another BU. Security-on only
        // (it supplies the owner's BU); with security off the store's existing stamping is unchanged.
        private void RealignOwningBusinessUnit(Entity stored)
        {
            if (_security == null || !stored.Contains(SystemFieldApplier.OwnerId) ||
                stored[SystemFieldApplier.OwnerId] is not EntityReference owner)
            {
                return;
            }

            Guid ownerBusinessUnit = _security.BusinessUnitOfPrincipal(owner);
            if (ownerBusinessUnit != Guid.Empty)
            {
                stored[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", ownerBusinessUnit);
            }
        }

        private SystemActor Actor()
        {
            // When security is on, the acting user's business unit comes from its registration, so records the
            // user creates are stamped with the BU that depth checks resolve against. Off: unchanged behaviour.
            Guid businessUnit = _currentBusinessUnitId;
            if (_security != null)
            {
                Guid registered = _security.BusinessUnitOf(_currentUserId);
                if (registered != Guid.Empty)
                {
                    businessUnit = registered;
                }
            }

            return new SystemActor(_currentUserId, businessUnit, _currentOrganizationId);
        }

        // The identity stamped into the audit and owner columns. As SYSTEM (ctx.SystemService) it is the SYSTEM user in
        // the root business unit, with the calling user as createdonbehalfby (verified live); otherwise the acting user.
        private SystemActor StampingActor()
        {
            SystemActor actor = Actor();
            return _actingAsSystem
                ? new SystemActor(SystemUserId, RootBusinessUnit(actor), actor.OrganizationId, onBehalfOfUserId: actor.UserId)
                : actor;
        }

        // WhoAmI inside a plugin returns the SYSTEM user on every service, whether created for null, the context's
        // UserId or Guid.Empty (verified live). The store runs on behalf of a plugin's service inside RunAs; the
        // test's own calls (outside any plugin) see the configured current user.
        private SystemActor WhoAmIActor()
        {
            SystemActor actor = Actor();
            return _actingAsSystem || _runAsDepth > 0
                ? new SystemActor(SystemUserId, RootBusinessUnit(actor), actor.OrganizationId)
                : actor;
        }

        // The root business unit: the top of the security model's hierarchy, else the acting user's business unit.
        private Guid RootBusinessUnit(SystemActor actor) => _security?.RootBusinessUnitId() ?? actor.BusinessUnitId;

        /// <summary>
        /// The acting identity (user, its business unit, and organization) the pipeline stamps onto a plugin
        /// execution context. Kept consistent with what <c>WhoAmI</c> reports and how <c>createdby</c>/<c>ownerid</c>
        /// are stamped, so a plugin reading <c>ctx.UserId</c>/<c>InitiatingUserId</c>/<c>BusinessUnitId</c>/
        /// <c>OrganizationId</c> sees the configured caller (<see cref="WithCurrentUser"/>), not a random value.
        /// </summary>
        public (Guid UserId, Guid BusinessUnitId, Guid OrganizationId) CurrentActor()
        {
            SystemActor actor = Actor();
            return (actor.UserId, actor.BusinessUnitId, actor.OrganizationId);
        }

        // Set while the store runs on behalf of the SYSTEM identity (a plugin's ctx.SystemService, i.e.
        // IOrganizationServiceFactory.CreateOrganizationService(null)): SYSTEM is not subject to the security model,
        // so every privilege / field-security / read-filter check is skipped, and the audit/owner columns are stamped
        // with the SYSTEM user (see StampingActor).
        private bool _actingAsSystem;

        // How many RunAs scopes are open: above zero, the store runs on behalf of a plugin's service.
        private int _runAsDepth;

        // The id the store stands the org's SYSTEM user in with: stamped into createdby/modifiedby/ownerid by a write
        // through the SYSTEM service and returned by WhoAmI inside a plugin.
        internal static Guid SystemUserId => PluginPipeline.SystemUserId;

        // The security model to ENFORCE for the current call: null when no model is configured or when acting as
        // SYSTEM. Stamping (Actor, owning-business-unit realignment) keeps reading _security directly.
        private SecurityModel? Enforcing => _actingAsSystem ? null : _security;

        // Runs <paramref name="body"/> as another identity: a specific user (privileges, field security, EqualUserId
        // and audit stamping resolve against that user), or SYSTEM when <paramref name="userId"/> is null (no
        // security enforcement). The previous identity is restored afterwards, so nested calls compose. Used by the
        // pipeline to give each plugin's ctx.Service / ctx.SystemService the identity it has on a real org.
        internal T RunAs<T>(Guid? userId, Func<T> body)
        {
            Guid previousUser = _currentUserId;
            bool previousSystem = _actingAsSystem;
            _runAsDepth++;
            if (userId.HasValue)
            {
                _currentUserId = userId.Value;
                _actingAsSystem = false;
            }
            else
            {
                _actingAsSystem = true;
            }

            try
            {
                return body();
            }
            finally
            {
                _currentUserId = previousUser;
                _actingAsSystem = previousSystem;
                _runAsDepth--;
            }
        }

        // Step images are masked like a Retrieve by the current user (the pipeline switches to the impersonated user
        // first, when the step has one): a secured column that user cannot read is absent from the pre- and
        // post-images (verified live, 2026-10-11 and 2026-11-18).
        internal void MaskSecuredImageColumns(Entity image) => MaskSecuredColumns(image);

        // Field-level security: drop field-secured columns the current user may not read from a returned record.
        private void MaskSecuredColumns(Entity entity)
        {
            if (Enforcing == null)
            {
                return;
            }

            List<string> hidden = entity.Attributes.Keys
                .Where(column => IsSecuredAndUnreadable(entity, column))
                .ToList();
            foreach (string column in hidden)
            {
                entity.Attributes.Remove(column);
            }
        }

        // Evaluate field-level security against the column's real table/field. Aliased (link-entity) columns are
        // keyed "alias.field" and typed as AliasedValue carrying the linked table/field, so testing the row's own
        // logical name (as a plain column would) leaves a joined secured column unmasked.
        private bool IsSecuredAndUnreadable(Entity entity, string column)
        {
            string table = entity.LogicalName;
            string field = column;
            if (entity[column] is AliasedValue aliased)
            {
                table = aliased.EntityLogicalName;
                field = aliased.AttributeLogicalName;
            }

            return IsUnreadableSecuredColumn(table, field);
        }

        // Whether the column is field-secured and the current user holds no read permission on it. Assumes security
        // is enforced.
        private bool IsUnreadableSecuredColumn(string entity, string column) =>
            !string.IsNullOrEmpty(column) && _security!.IsSecuredColumn(entity, column) && !_security.CanReadColumn(_currentUserId, entity, column);

        // Field-level security: reject a RetrieveMultiple that aggregates or groups by a field-secured column the
        // caller can't read (the aggregate value would otherwise leak it).
        private void DemandAggregateColumnReads(QueryBase query)
        {
            if (Enforcing == null)
            {
                return;
            }

            // FetchXML is checked through its parsed QueryExpression, so both forms are covered identically, including
            // group-by/aggregate columns on link-entities (checked against the linked table).
            QueryExpression? expression = query as QueryExpression
                ?? (query is FetchExpression fetch ? FetchXmlParser.Parse(fetch.Query).Query : null);
            if (expression == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> column in InMemoryQueryEngine.AttributeExpressionColumns(expression))
            {
                DemandSecuredColumnRead(column.Key, column.Value);
            }
        }

        // Field-level security for a single-column read: the file-download messages stream a File column's content, so
        // they must honour the same secured-column read check that Retrieve applies via MaskSecuredColumns. Throws
        // when the column is field-secured and the current user cannot read it.
        private void DemandSecuredColumnRead(string entity, string column)
        {
            if (Enforcing == null)
            {
                return;
            }

            if (IsUnreadableSecuredColumn(entity, column))
            {
                throw _security!.PrivilegeDenied(_currentUserId, Privilege.Read, $"{entity}.{column}");
            }
        }

        // Field-level security: reject a Create that sets a field-secured column the caller has no create permission
        // on, or an Update that sets one the caller has no update permission on (a column security profile grants the
        // two separately).
        private void DemandSecuredColumnWrites(Entity entity, bool onCreate)
        {
            if (Enforcing == null)
            {
                return;
            }

            foreach (string column in entity.Attributes.Keys)
            {
                if (!_security!.IsSecuredColumn(entity.LogicalName, column))
                {
                    continue;
                }

                bool allowed = onCreate
                    ? _security.CanCreateColumn(_currentUserId, entity.LogicalName, column)
                    : _security.CanUpdateColumn(_currentUserId, entity.LogicalName, column);
                if (!allowed)
                {
                    throw _security.PrivilegeDenied(_currentUserId, onCreate ? Privilege.Create : Privilege.Write, $"{entity.LogicalName}.{column}");
                }
            }
        }

        // Enforces a privilege for the current user when security is configured; a no-op otherwise (default-off).
        // Access is the union of role access, sharing and the inherited grants a parental child received from its
        // parent (all in the SecurityModel).
        private void DemandAccess(string entityName, Privilege privilege, Entity? record)
        {
            if (Enforcing == null)
            {
                DemandStrictSecurityConfigured();
                return;
            }

            if (CanAccess(entityName, privilege, record))
            {
                return;
            }

            throw _security!.PrivilegeDenied(_currentUserId, privilege, entityName);
        }

        // WithStrictSecurity guard: a security-relevant operation run with no configured security model fails loudly
        // instead of silently allowing everything, so an access-control test that forgot WithSecurity(...) cannot pass
        // green. No-op unless strict mode was opted into and no security model is set.
        private void DemandStrictSecurityConfigured()
        {
            if (_strictSecurity && _security == null)
            {
                throw new InvalidOperationException(
                    "Strict security is enabled (WithStrictSecurity) but no security model is configured. Call " +
                    "WithSecurity(...) to define roles/privileges for this scenario, or remove WithStrictSecurity.");
            }
        }

        // The full access decision for the current user: role access plus record-level access (explicit shares,
        // inherited grants, access teams), all evaluated by the security model. Assumes security is configured.
        // Verified live: AddUserToRecordTeam refuses a user whose roles lack a privilege the template grants
        // (0x80048342), at any depth. Off is a no-op.
        private void DemandTemplatePrivileges(string entityName, Guid userId, AccessRights rights)
        {
            if (Enforcing?.MissingTemplatePrivilege(userId, entityName, rights) is Privilege missing)
            {
                throw PlatformFault.Create(PlatformFault.AccessTeamMemberLacksPrivilege,
                    $"The user can't be added to the team because the user doesn't have the 'prv{missing}{entityName}' privilege.");
            }
        }

        private bool CanAccess(string entityName, Privilege privilege, Entity? record) =>
            _security!.IsGranted(_currentUserId, entityName, privilege, record);

        // Reparent cascade (verified live, 2026-10-19): when a child is created under a parental parent, or its lookup
        // is moved to one, it gets inherited grants for the parent's OWNER (bounded by the owner's effective rights on
        // the parent — never Create) and for every principal the parent is ALREADY shared with, explicitly or by
        // inheritance, with that grant's mask. The grants from the previous parent are dropped first. A share made on
        // the parent later does not flow down here (only the Share cascade carries it; a WithSharedRecord re-derives the
        // seeded rows, see DeriveSeededAccess). Only the record whose parent changed is re-derived: its existing
        // descendants keep their grants (multi-level re-derivation is not modelled). Role access to the parent confers
        // nothing. Needs a security model to compute the owner's rights.
        private void DeriveInheritedAccess(Entity child)
        {
            if (_security == null)
            {
                return;
            }

            EntityReference childRef = new EntityReference(child.LogicalName, child.Id);
            _sharing.ClearInherited(childRef);
            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ParentRelationshipsOf(child.LogicalName))
            {
                CascadeType reparent = relationship.Cascade.Reparent ?? CascadeType.NoCascade;
                if (reparent == CascadeType.NoCascade ||
                    child.GetAttributeValue<EntityReference>(relationship.ReferencingAttribute) is not EntityReference parentRef ||
                    !string.Equals(parentRef.LogicalName, relationship.ReferencedEntity, StringComparison.OrdinalIgnoreCase) ||
                    StoredOrNull(parentRef.LogicalName, parentRef.Id) is not Entity parent)
                {
                    continue;
                }

                EntityReference? owner = parent.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId);
                if (!MatchesCascadeScope(child, reparent, owner))
                {
                    continue;
                }

                if (owner != null)
                {
                    AccessRights ownerRights = _security.EffectiveAccess(owner.Id, parentRef.LogicalName, parent);
                    _sharing.GrantInherited(childRef, new PrincipalAccess { Principal = owner, AccessMask = ownerRights });
                }

                foreach (PrincipalAccess share in _sharing.SharesOf(parentRef).Concat(_sharing.InheritedOf(parentRef)))
                {
                    _sharing.GrantInherited(childRef, share);
                }
            }
        }

        // Re-derives the inherited access of every seeded row still derived from the setup (see _seededRows), each after
        // the seeded parent it inherits from, so neither the setup order nor the order of the rows in a Seed matters.
        // Clearing and re-deriving makes a repeat call idempotent: no grant is counted twice.
        private void DeriveSeededAccess()
        {
            HashSet<Guid> derived = new HashSet<Guid>();
            foreach (KeyValuePair<Guid, string> row in _seededRows)
            {
                DeriveSeededAccess(row.Value, row.Key, derived);
            }
        }

        private void DeriveSeededAccess(string entityName, Guid id, HashSet<Guid> derived)
        {
            // A row is derived once per pass (which also stops a cycle of parents); a row deleted since is skipped.
            if (!derived.Add(id) || StoredOrNull(entityName, id) is not Entity row)
            {
                return;
            }

            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ParentRelationshipsOf(entityName))
            {
                if (row.GetAttributeValue<EntityReference>(relationship.ReferencingAttribute) is EntityReference parent &&
                    _seededRows.TryGetValue(parent.Id, out string? parentEntity))
                {
                    DeriveSeededAccess(parentEntity, parent.Id, derived);
                }
            }

            DeriveInheritedAccess(row);
        }

        // Re-derives the record's inherited access when an Update moved one of its Reparent-cascading lookups to
        // another record (or cleared it). <paramref name="previous"/> holds the lookups' values before the update;
        // record ids are unique across tables, so comparing ids also catches a polymorphic lookup switching table.
        private void DeriveInheritedAccessIfReparented(Entity stored, Dictionary<string, EntityReference?> previous)
        {
            foreach (KeyValuePair<string, EntityReference?> lookup in previous)
            {
                if (stored.GetAttributeValue<EntityReference>(lookup.Key)?.Id != lookup.Value?.Id)
                {
                    DeriveInheritedAccess(stored);
                    return;
                }
            }
        }

        // The current values of the record's Reparent-cascading lookups, captured before an Update so a change of
        // parent can be detected afterwards.
        private Dictionary<string, EntityReference?> ReparentLookups(Entity stored)
        {
            Dictionary<string, EntityReference?> lookups = new Dictionary<string, EntityReference?>(StringComparer.OrdinalIgnoreCase);
            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ParentRelationshipsOf(stored.LogicalName))
            {
                if ((relationship.Cascade.Reparent ?? CascadeType.NoCascade) != CascadeType.NoCascade)
                {
                    lookups[relationship.ReferencingAttribute] = stored.GetAttributeValue<EntityReference>(relationship.ReferencingAttribute);
                }
            }

            return lookups;
        }

        // Create is checked at depth against the record's PROSPECTIVE owner/BU, exactly like Read/Write/Delete are
        // checked against an existing record — so a User-level Create only covers records the caller will own, a
        // BusinessUnit-level Create covers the caller's BU, etc. Creating for another principal is also an
        // assignment, so it additionally needs Assign. No-op when security is off.
        private void DemandCreate(Entity entity)
        {
            EntityReference owner =
                entity.Contains(SystemFieldApplier.OwnerId) && entity[SystemFieldApplier.OwnerId] is EntityReference explicitOwner
                    ? explicitOwner
                    : new EntityReference(PrincipalKind.SystemUser, _currentUserId);

            if (Enforcing == null)
            {
                DemandStrictSecurityConfigured();
                DemandOwnerCanRead(entity.LogicalName, owner, onCreate: true);
                return;
            }

            Entity prospective = new Entity(entity.LogicalName) { [SystemFieldApplier.OwnerId] = owner };
            Guid ownerBusinessUnit = _security!.BusinessUnitOfPrincipal(owner);
            if (ownerBusinessUnit != Guid.Empty)
            {
                prospective[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", ownerBusinessUnit);
            }

            _security.Demand(_currentUserId, entity.LogicalName, Privilege.Create, prospective);

            if (!PrincipalKind.IsUser(owner, _currentUserId))
            {
                // Pass the prospective record (with its owner/BU set) so Assign is depth/BU-checked against the
                // record's target owner, exactly as a real org checks an assignment — not treated as a blanket grant.
                _security.Demand(_currentUserId, entity.LogicalName, Privilege.Assign, prospective);
            }

            DemandOwnerCanRead(entity.LogicalName, owner, onCreate: true);
        }

        // A record's owner must hold Read on its table, whoever sets it (Create with ownerid, an ownerid Update, Assign,
        // or a PreOperation plugin writing Target["ownerid"]) — verified live. The check is on the owner, not the
        // caller, so it also runs as SYSTEM; it needs a configured model and an owner the model knows, and skips the
        // caller owning its own record.
        private void DemandOwnerCanRead(string entityName, EntityReference? owner, bool onCreate)
        {
            if (_security == null || owner == null || PrincipalKind.IsUser(owner, _currentUserId))
            {
                return;
            }

            FaultException<OrganizationServiceFault>? denied = _security.OwnerReadDenied(owner, entityName, onCreate);
            if (denied != null)
            {
                throw denied;
            }
        }

        // Associating or disassociating over an N:N relationship needs Append on the records of both tables (Learn,
        // security roles and privileges: "For many-to-many relationships, a user must have Append privilege for both
        // tables being associated or disassociated"), whichever side the call is made from. Off is a no-op.
        private void DemandManyToManyAccess(string entityName, Guid entityId, EntityReferenceCollection relatedEntities)
        {
            if (Enforcing == null)
            {
                return;
            }

            DemandAccess(entityName, Privilege.Append, StoredOrNull(entityName, entityId));
            foreach (EntityReference related in relatedEntities)
            {
                DemandAccess(related.LogicalName, Privilege.Append, StoredOrNull(related.LogicalName, related.Id));
            }
        }

        // A 1:N Associate/Disassociate made from the parent (referenced) side needs AppendTo on the parent and Append on
        // each child before the children's lookups are written. From the child side no pre-check runs: the call is the
        // Update of the child's lookup, which demands Write and Append on the child and, when it sets the lookup,
        // AppendTo on the parent. Off is a no-op.
        private void DemandOneToManyAccess(string entityName, Guid entityId, OneToManyRegistry.OneToMany relationship, EntityReferenceCollection relatedEntities)
        {
            if (Enforcing == null || !string.Equals(entityName, relationship.ReferencedEntity, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DemandAccess(entityName, Privilege.AppendTo, StoredOrNull(entityName, entityId));
            foreach (EntityReference related in relatedEntities)
            {
                DemandAccess(related.LogicalName, Privilege.Append, StoredOrNull(related.LogicalName, related.Id));
            }
        }

        private Entity? StoredOrNull(string entityName, Guid id) =>
            Table(entityName).TryGetValue(id, out Entity? stored) ? stored : null;

        // System owner/audit references are not relationship lookups: ownership is governed by Assign, and the
        // audit columns are server-set — none of them go through Append/AppendTo.
        private static readonly HashSet<string> SystemReferenceColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SystemFieldApplier.OwnerId, SystemFieldApplier.OwningUser, SystemFieldApplier.OwningTeam,
            SystemFieldApplier.OwningBusinessUnit, SystemFieldApplier.CreatedBy, SystemFieldApplier.ModifiedBy,
            SystemFieldApplier.OrganizationId,
        };

        // 0x80040502 ObjectDoesNotExistInRelationship-style fault an Update raises for a lookup to a missing record.
        private const int UpdateLookupTargetDoesNotExist = unchecked((int)0x80040502);

        // With security on, a lookup set by id must point at an existing record (verified live: Create faults
        // 0x80040217 "Entity 'X' With Id = ... Does Not Exist", Update faults 0x80040502 "X does not exist."), so the
        // AppendTo check below never runs against a missing record. Security off keeps dangling lookups: a
        // single-plugin PluginContext commonly writes a lookup to a Target it never seeded. The system columns the
        // store fills or re-derives (owner, created/modified by, organization) are not checked. A lookup set by key
        // reaches this check already resolved to its record's id (WithResolvedLookupKeys), so an empty id here is a
        // lookup to no record and faults.
        private void DemandLookupTargetsExist(Entity entity, bool onCreate)
        {
            // Configured security, not the acting identity: SYSTEM cannot point a lookup at a missing record either.
            if (_security == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                if (attribute.Value is not EntityReference target
                    || SystemReferenceColumns.Contains(attribute.Key)
                    || (onCreate && target.Id != Guid.Empty && target.Id == entity.Id && string.Equals(target.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                    || StoredOrNull(target.LogicalName, target.Id) != null)
                {
                    continue;
                }

                throw onCreate
                    ? PlatformFault.Create(ObjectDoesNotExist, $"Entity '{target.LogicalName}' With Id = {target.Id:D} Does Not Exist")
                    : PlatformFault.Create(UpdateLookupTargetDoesNotExist, $"{target.LogicalName} does not exist.");
            }
        }

        // Setting a lookup relates two records, which needs Append on the record carrying the lookup and AppendTo on
        // the lookup's target record (Dataverse). No-op when security is off. <paramref name="self"/> is the record
        // carrying the lookup (the stored row on Update; null on Create, where it does not exist yet).
        private void DemandLookupAppends(Entity entity, Entity? self)
        {
            if (Enforcing == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                if (attribute.Value is EntityReference target && !SystemReferenceColumns.Contains(attribute.Key))
                {
                    DemandAccess(entity.LogicalName, Privilege.Append, self);
                    DemandAccess(target.LogicalName, Privilege.AppendTo, StoredOrNull(target.LogicalName, target.Id));
                }
            }
        }

        // The effective rights a principal holds on a record (roles + ownership + sharing). With no security model
        // everyone has full record access, so every shareable right is reported.
        private AccessRights EffectiveAccess(EntityReference principal, EntityReference target)
        {
            // A record that does not exist confers no access, whether or not security is on (consistent answer).
            Entity? stored = StoredOrNull(target.LogicalName, target.Id);
            if (stored == null)
            {
                return AccessRights.None;
            }

            if (_security == null)
            {
                return AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.DeleteAccess |
                       AccessRights.AppendAccess | AccessRights.AppendToAccess | AccessRights.AssignAccess | AccessRights.ShareAccess;
            }

            return _security.EffectiveAccess(principal.Id, target.LogicalName, stored);
        }

        // A user can only share rights they themselves hold on the record (Dataverse: "you can't give rights you
        // don't have"), so a runtime GrantAccess/ModifyAccess is clamped to the sharer's effective rights. Off: no clamp.
        private PrincipalAccess ClampToSharerRights(EntityReference target, PrincipalAccess access)
        {
            // The sharer's effective rights include role, ownership, sharing AND inherited access — reuse the same
            // computation RetrievePrincipalAccess uses. With security off this returns full rights, so the clamp is a
            // no-op (unchanged behaviour). SYSTEM holds every right, so a share issued as SYSTEM is never clamped.
            if (_actingAsSystem)
            {
                return access;
            }

            AccessRights sharerRights = EffectiveAccess(new EntityReference(PrincipalKind.SystemUser, _currentUserId), target);
            return new PrincipalAccess { Principal = access.Principal, AccessMask = access.AccessMask & sharerRights };
        }
        // Applies each child 1:N relationship's Delete cascade, then removes the record via <paramref name="removeRecord"/>:
        // Cascade deletes the children (recursively), RemoveLink clears their lookup, Restrict blocks the delete while
        // any child exists. Cascaded child deletes are system operations — they do NOT re-check the caller's Delete
        // privilege (only the parent's is validated). The visited set guards against cycles.
        private void CascadeDelete(string entityName, Guid id, HashSet<Guid> visited, Action<string, Guid> removeRecord)
        {
            if (!visited.Add(id))
            {
                return;
            }

            List<OneToManyRegistry.OneToMany> childRelationships = _oneToMany.ChildRelationshipsOf(entityName).ToList();

            // Restrict is evaluated first: a single restricting child blocks the whole delete.
            foreach (OneToManyRegistry.OneToMany relationship in childRelationships)
            {
                if (relationship.Cascade.Delete == CascadeType.Restrict && Children(relationship, id).Any())
                {
                    // 0x80040227, the platform's fault for a delete a Restrict relationship blocks (verified live).
                    throw PlatformFault.Create(unchecked((int)0x80040227),
                        $"The object you tried to delete is associated with another object and cannot be deleted. Deleting {entityName} {id}: "
                        + $"related {relationship.ReferencingEntity} records exist and the relationship's Delete behaviour is Restrict.");
                }
            }

            foreach (OneToManyRegistry.OneToMany relationship in childRelationships)
            {
                List<Entity> children = Children(relationship, id).ToList();
                if (relationship.Cascade.Delete == CascadeType.Cascade)
                {
                    foreach (Entity child in children)
                    {
                        CascadeDelete(relationship.ReferencingEntity, child.Id, visited, removeRecord);
                    }
                }
                else if (relationship.Cascade.Delete == CascadeType.RemoveLink)
                {
                    foreach (Entity child in children)
                    {
                        child.Attributes.Remove(relationship.ReferencingAttribute);
                    }
                }
            }

            // Clean up the record's N:N intersect rows so deleting one side of a many-to-many does not leave dangling
            // intersect rows pointing at a record that no longer exists (Dataverse removes them on delete).
            RemoveDanglingIntersectRows(entityName, id);

            removeRecord(entityName, id);
        }

        // Removes intersect rows of any declared or metadata-declared N:N relationship that reference the record being
        // deleted (on either side), so no intersect row is left pointing at a deleted record.
        private void RemoveDanglingIntersectRows(string entityName, Guid id)
        {
            foreach (RelationshipRegistry.ManyToMany m2m in AllManyToManyRelationships())
            {
                bool isFirst = string.Equals(m2m.FirstEntity, entityName, StringComparison.OrdinalIgnoreCase);
                bool isSecond = string.Equals(m2m.SecondEntity, entityName, StringComparison.OrdinalIgnoreCase);
                if (!isFirst && !isSecond)
                {
                    continue;
                }

                string attribute = isFirst ? m2m.FirstAttribute : m2m.SecondAttribute;
                Dictionary<Guid, Entity> intersect = Table(m2m.IntersectEntity);
                List<Guid> toRemove = intersect.Values
                    .Where(row => EntityValues.AreEqual(EntityValues.GetValue(row, attribute), id))
                    .Select(row => row.Id)
                    .ToList();
                foreach (Guid rowId in toRemove)
                {
                    intersect.Remove(rowId);
                }
            }
        }

        // Merge moves the subordinate's N:N associations to the master (verified live): each intersect row naming
        // the subordinate names the master instead, unless the master already has that association, in which case
        // the subordinate's row is dropped. On a self-referential N:N, a row linking the subordinate to the master
        // would become a master-to-master self-link, so it is dropped too.
        private void MoveIntersectRows(string entityName, Guid fromId, Guid toId)
        {
            foreach (RelationshipRegistry.ManyToMany m2m in AllManyToManyRelationships())
            {
                Dictionary<Guid, Entity> intersect = Table(m2m.IntersectEntity);
                bool selfReferential = string.Equals(m2m.FirstEntity, m2m.SecondEntity, StringComparison.OrdinalIgnoreCase);
                foreach ((string attribute, string otherAttribute) in new[] { (m2m.FirstAttribute, m2m.SecondAttribute), (m2m.SecondAttribute, m2m.FirstAttribute) })
                {
                    string side = attribute == m2m.FirstAttribute ? m2m.FirstEntity : m2m.SecondEntity;
                    if (!string.Equals(side, entityName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (Entity row in intersect.Values.Where(row => EntityValues.AreEqual(EntityValues.GetValue(row, attribute), fromId)).ToList())
                    {
                        object? other = EntityValues.GetValue(row, otherAttribute);
                        bool masterHasIt = intersect.Values.Any(existing =>
                            EntityValues.AreEqual(EntityValues.GetValue(existing, attribute), toId)
                            && EntityValues.AreEqual(EntityValues.GetValue(existing, otherAttribute), other));
                        if (masterHasIt || (selfReferential && EntityValues.AreEqual(other, toId)))
                        {
                            intersect.Remove(row.Id);
                        }
                        else
                        {
                            row[attribute] = new EntityReference(entityName, toId);
                        }
                    }
                }
            }
        }

        // The child records whose lookup points at the given parent id.
        private IEnumerable<Entity> Children(OneToManyRegistry.OneToMany relationship, Guid parentId) =>
            Table(relationship.ReferencingEntity).Values
                .Where(child => child.GetAttributeValue<EntityReference>(relationship.ReferencingAttribute)?.Id == parentId);

        // Cascades an ownership change from a parent to its children per each relationship's Assign behaviour:
        // Cascade = all children, Active = only active children, UserOwned = only children owned by the same user as
        // the parent was. The child's owning business unit follows the new owner (when security supplies it), and the
        // cascade recurses down the hierarchy.
        private void CascadeAssign(string entityName, Guid id, EntityReference? previousOwner, EntityReference newOwner, HashSet<Guid> visited)
        {
            if (!visited.Add(id))
            {
                return;
            }

            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ChildRelationshipsOf(entityName))
            {
                CascadeType mode = relationship.Cascade.Assign ?? CascadeType.NoCascade;
                if (mode == CascadeType.NoCascade)
                {
                    continue;
                }

                foreach (Entity child in Children(relationship, id).ToList())
                {
                    if (!MatchesCascadeScope(child, mode, previousOwner))
                    {
                        continue;
                    }

                    EntityReference? childPreviousOwner = child.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId);
                    ApplyCascadedOwner(child, newOwner);
                    CascadeAssign(relationship.ReferencingEntity, child.Id, childPreviousOwner, newOwner, visited);
                }
            }
        }

        // Whether a child is in scope for a cascade of the given mode: Cascade = always, Active = only active
        // children, UserOwned = only children owned by the same principal as the referenced (parent) record.
        private static bool MatchesCascadeScope(Entity child, CascadeType mode, EntityReference? referencedOwner)
        {
            switch (mode)
            {
                case CascadeType.Active: return IsActive(child);
                case CascadeType.UserOwned: return OwnedBySamePrincipal(child, referencedOwner);
                default: return true;
            }
        }

        // Cascades a share (GrantAccess/ModifyAccess) from a parent to its children per the Share cascade mode, then
        // recurses down the hierarchy. The children get an INHERITED grant, not an explicit share (verified live: the
        // PoA row carries only the inherited mask), so it is not listed by RetrieveSharedPrincipalsAndAccess. A
        // GrantAccess adds to the principal's inherited rights, a ModifyAccess (<paramref name="replace"/>) replaces them.
        private void CascadeShare(string entityName, Guid id, PrincipalAccess access, bool replace, HashSet<Guid> visited)
        {
            if (!visited.Add(id))
            {
                return;
            }

            EntityReference? owner = StoredOrNull(entityName, id)?.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId);
            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ChildRelationshipsOf(entityName))
            {
                CascadeType mode = relationship.Cascade.Share ?? CascadeType.NoCascade;
                if (mode == CascadeType.NoCascade)
                {
                    continue;
                }

                foreach (Entity child in Children(relationship, id).ToList())
                {
                    if (!MatchesCascadeScope(child, mode, owner))
                    {
                        continue;
                    }

                    EntityReference childRef = new EntityReference(relationship.ReferencingEntity, child.Id);
                    _seededRows.Remove(child.Id); // its grants now come from a run-time cascade too
                    if (replace)
                    {
                        _sharing.ModifyInherited(childRef, access);
                    }
                    else
                    {
                        _sharing.GrantInherited(childRef, access);
                    }

                    CascadeShare(relationship.ReferencingEntity, child.Id, access, replace, visited);
                }
            }
        }

        // Cascades an unshare (RevokeAccess) from a parent to its children per the Unshare cascade mode: it removes the
        // principal's INHERITED grant only — a share made on the child itself is kept (Learn: removing a parent's share
        // removes what the children inherited from it).
        private void CascadeUnshare(string entityName, Guid id, EntityReference principal, HashSet<Guid> visited)
        {
            if (!visited.Add(id))
            {
                return;
            }

            EntityReference? owner = StoredOrNull(entityName, id)?.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId);
            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ChildRelationshipsOf(entityName))
            {
                CascadeType mode = relationship.Cascade.Unshare ?? CascadeType.NoCascade;
                if (mode == CascadeType.NoCascade)
                {
                    continue;
                }

                foreach (Entity child in Children(relationship, id).ToList())
                {
                    if (!MatchesCascadeScope(child, mode, owner))
                    {
                        continue;
                    }

                    _seededRows.Remove(child.Id);
                    _sharing.RevokeInherited(new EntityReference(relationship.ReferencingEntity, child.Id), principal);
                    CascadeUnshare(relationship.ReferencingEntity, child.Id, principal, visited);
                }
            }
        }

        // Active state is state code 0 for most tables (the common case the harness models); a missing state is active.
        private static bool IsActive(Entity record) =>
            (record.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0) == 0;

        private static bool OwnedBySamePrincipal(Entity record, EntityReference? owner) =>
            owner != null && record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId)?.Id == owner.Id;

        // Sets the owner columns on a cascaded child: ownerid + owninguser/owningteam, and owningbusinessunit from the
        // new owner's business unit when security supplies it (owner move follows the owner into their BU).
        private void ApplyCascadedOwner(Entity child, EntityReference newOwner)
        {
            child[SystemFieldApplier.OwnerId] = newOwner;
            if (PrincipalKind.IsTeam(newOwner))
            {
                child[SystemFieldApplier.OwningTeam] = new EntityReference(PrincipalKind.Team, newOwner.Id);
                child.Attributes.Remove(SystemFieldApplier.OwningUser);
            }
            else
            {
                child[SystemFieldApplier.OwningUser] = new EntityReference(PrincipalKind.SystemUser, newOwner.Id);
                child.Attributes.Remove(SystemFieldApplier.OwningTeam);
            }

            if (_security != null)
            {
                Guid businessUnit = _security.BusinessUnitOfPrincipal(newOwner);
                if (businessUnit != Guid.Empty)
                {
                    child[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", businessUnit);
                }
            }
        }
        // The predicate the query engine uses to keep only rows the current user may read (null = no security).
        // Includes the record's inherited grants, and reads the stored record's own owner columns so
        // filtering is correct regardless of the query's ColumnSet.
        private Func<Entity, bool>? ReadFilter() =>
            Enforcing == null ? null : row => MembershipIntersectTables.Contains(row.LogicalName) || CanAccess(row.LogicalName, Privilege.Read, row);
    }
}
