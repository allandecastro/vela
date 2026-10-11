using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
        // The platform's security-membership tables plugins query to resolve roles. Seeded from the security model
        // (below) and always readable (global read on these is the platform default) — see ReadFilter / Retrieve.
        private static readonly HashSet<string> SecurityInfraTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "role", "systemuserroles", "teamroles", "teammembership",
        };

        // Rebuild the role/systemuserroles/teamroles/teammembership rows from the security model, so a plugin can
        // query role membership like the platform (e.g. RetrieveMultiple over systemuserroles, or a join to role).
        private void MaterializeSecurityTables()
        {
            foreach (string table in SecurityInfraTables)
            {
                _store.Remove(table);
            }

            if (_security == null)
            {
                return;
            }

            foreach (Entity row in _security.MaterializeMembershipTables())
            {
                Table(row.LogicalName)[row.Id] = row;
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
        // so every privilege / field-security / read-filter check is skipped. Audit/owner stamping still uses the
        // calling user (and its registered business unit), so the configured security model is not torn down.
        private bool _actingAsSystem;

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
            }
        }

        // Step images are masked like a Retrieve by the calling user: a secured column that user cannot read is absent
        // from the pre- and post-images (verified live, 2026-10-11).
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

            return _security!.IsSecuredColumn(table, field) && !_security.CanReadColumn(_currentUserId, table, field);
        }

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
                DemandSecuredAggregateColumn(column.Key, column.Value);
            }
        }

        private void DemandSecuredAggregateColumn(string entity, string column)
        {
            if (!string.IsNullOrEmpty(column) && _security!.IsSecuredColumn(entity, column) && !_security.CanReadColumn(_currentUserId, entity, column))
            {
                throw _security.PrivilegeDenied(_currentUserId, Privilege.Read, $"{entity}.{column}");
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

            if (!string.IsNullOrEmpty(column) && _security!.IsSecuredColumn(entity, column) && !_security.CanReadColumn(_currentUserId, entity, column))
            {
                throw _security.PrivilegeDenied(_currentUserId, Privilege.Read, $"{entity}.{column}");
            }
        }

        // Field-level security: reject a Create/Update that sets a field-secured column the caller can't write.
        private void DemandSecuredColumnWrites(Entity entity)
        {
            if (Enforcing == null)
            {
                return;
            }

            foreach (string column in entity.Attributes.Keys)
            {
                if (_security!.IsSecuredColumn(entity.LogicalName, column) && !_security.CanWriteColumn(_currentUserId, entity.LogicalName, column))
                {
                    throw _security.PrivilegeDenied(_currentUserId, Privilege.Write, $"{entity.LogicalName}.{column}");
                }
            }
        }

        // Enforces a privilege for the current user when security is configured; a no-op otherwise (default-off).
        // Access is the union of role access, sharing (both in the SecurityModel) and inherited access from a
        // parental parent record — so a user who can act on the parent can act on the child.
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

        // The full access decision for the current user: role + shared access, or access inherited from the record's
        // parent in a parental relationship. Assumes security is configured.
        private bool CanAccess(string entityName, Privilege privilege, Entity? record) =>
            _security!.IsGranted(_currentUserId, entityName, privilege, record) ||
            HasInheritedAccess(_currentUserId, entityName, privilege, record, new HashSet<Guid>());

        // Inherited access: in a parental relationship (one that cascades Reparent), a child inherits the access the
        // user has on its parent — for every privilege except Create. Walks up the hierarchy, guarding against cycles.
        private bool HasInheritedAccess(Guid userId, string entityName, Privilege privilege, Entity? record, HashSet<Guid> visited)
        {
            if (_security == null || record == null || privilege == Privilege.Create || !visited.Add(record.Id))
            {
                return false;
            }

            foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ParentRelationshipsOf(entityName))
            {
                if (!ConfersInheritedAccess(relationship) ||
                    record.GetAttributeValue<EntityReference>(relationship.ReferencingAttribute) is not EntityReference parentRef)
                {
                    continue;
                }

                Entity? parent = StoredOrNull(parentRef.LogicalName, parentRef.Id);
                if (parent != null &&
                    (_security.IsGranted(userId, parentRef.LogicalName, privilege, parent) ||
                     HasInheritedAccess(userId, parentRef.LogicalName, privilege, parent, visited)))
                {
                    return true;
                }
            }

            return false;
        }

        // A relationship confers inherited access when it cascades the Reparent action (the parental behaviour that
        // re-derives inherited rights); a purely referential relationship (Reparent = NoCascade) does not.
        private static bool ConfersInheritedAccess(OneToManyRegistry.OneToMany relationship) =>
            relationship.Cascade.Reparent is CascadeType reparent && reparent != CascadeType.NoCascade;

        // Create is checked at depth against the record's PROSPECTIVE owner/BU, exactly like Read/Write/Delete are
        // checked against an existing record — so a User-level Create only covers records the caller will own, a
        // BusinessUnit-level Create covers the caller's BU, etc. Creating for another principal is also an
        // assignment, so it additionally needs Assign. No-op when security is off.
        private void DemandCreate(Entity entity)
        {
            if (Enforcing == null)
            {
                DemandStrictSecurityConfigured();
                return;
            }

            EntityReference owner =
                entity.Contains(SystemFieldApplier.OwnerId) && entity[SystemFieldApplier.OwnerId] is EntityReference explicitOwner
                    ? explicitOwner
                    : new EntityReference("systemuser", _currentUserId);

            Entity prospective = new Entity(entity.LogicalName) { [SystemFieldApplier.OwnerId] = owner };
            Guid ownerBusinessUnit = _security!.BusinessUnitOfPrincipal(owner);
            if (ownerBusinessUnit != Guid.Empty)
            {
                prospective[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", ownerBusinessUnit);
            }

            _security.Demand(_currentUserId, entity.LogicalName, Privilege.Create, prospective);

            bool ownedByCaller = string.Equals(owner.LogicalName, "systemuser", StringComparison.OrdinalIgnoreCase) && owner.Id == _currentUserId;
            if (!ownedByCaller)
            {
                // Pass the prospective record (with its owner/BU set) so Assign is depth/BU-checked against the
                // record's target owner, exactly as a real org checks an assignment — not treated as a blanket grant.
                _security.Demand(_currentUserId, entity.LogicalName, Privilege.Assign, prospective);
            }
        }

        // Relating records needs AppendTo on the record being related to and Append on each record related to it,
        // mirroring the platform. Off (or nothing configured) is a no-op.
        private void DemandAssociationAccess(string entityName, Guid entityId, EntityReferenceCollection relatedEntities)
        {
            if (Enforcing == null)
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
        // store fills or re-derives (owner, created/modified by, organization) are not checked, and a lookup set by
        // key is resolved, and checked, separately.
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
                    || target.Id == Guid.Empty
                    || SystemReferenceColumns.Contains(attribute.Key)
                    || (onCreate && target.Id == entity.Id && string.Equals(target.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
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

            return _security.EffectiveAccess(principal.Id, target.LogicalName, stored,
                privilege => HasInheritedAccess(principal.Id, target.LogicalName, privilege, stored, new HashSet<Guid>()));
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

            AccessRights sharerRights = EffectiveAccess(new EntityReference("systemuser", _currentUserId), target);
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
        // the subordinate's row is dropped.
        private void MoveIntersectRows(string entityName, Guid fromId, Guid toId)
        {
            foreach (RelationshipRegistry.ManyToMany m2m in AllManyToManyRelationships())
            {
                Dictionary<Guid, Entity> intersect = Table(m2m.IntersectEntity);
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
                        if (masterHasIt)
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
        // recurses down the hierarchy. The sharer already has inherited share rights on the children (parental).
        private void CascadeShare(string entityName, Guid id, PrincipalAccess access, HashSet<Guid> visited)
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

                    _sharing.Grant(new EntityReference(relationship.ReferencingEntity, child.Id), access);
                    CascadeShare(relationship.ReferencingEntity, child.Id, access, visited);
                }
            }
        }

        // Cascades an unshare (RevokeAccess) from a parent to its children per the Unshare cascade mode.
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

                    _sharing.Revoke(new EntityReference(relationship.ReferencingEntity, child.Id), principal);
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
            if (string.Equals(newOwner.LogicalName, "team", StringComparison.OrdinalIgnoreCase))
            {
                child[SystemFieldApplier.OwningTeam] = new EntityReference("team", newOwner.Id);
                child.Attributes.Remove(SystemFieldApplier.OwningUser);
            }
            else
            {
                child[SystemFieldApplier.OwningUser] = new EntityReference("systemuser", newOwner.Id);
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
        // Includes access inherited from a parental parent, and reads the stored record's own owner columns so
        // filtering is correct regardless of the query's ColumnSet.
        private Func<Entity, bool>? ReadFilter() =>
            Enforcing == null ? null : row => SecurityInfraTables.Contains(row.LogicalName) || CanAccess(row.LogicalName, Privilege.Read, row);
    }
}
