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
    // Create (deep insert included), Retrieve, Update, Upsert and Delete on the store, with reference resolution by id or
    // alternate key.
    public sealed partial class InMemoryOrganizationService
    {
        public Guid Create(Entity entity)
        {
            // A deep insert (target carries RelatedEntities) is one transaction: if a child/grandchild fails, the
            // parent and earlier descendants roll back too, instead of leaving partial rows. A plain create needs no
            // snapshot. CreateRecordDirect throws ArgumentNullException for a null entity, preserving that guard.
            if (entity != null && entity.RelatedEntities.Count > 0)
            {
                return RunAtomically(() => CreateGraph(entity));
            }

            return CreateGraph(entity!);
        }

        private Guid CreateGraph(Entity entity)
        {
            Guid id = CreateRecordDirect(entity);

            // Deep insert: create the related records held in the target's RelatedEntities (1:N children), linking
            // each child back to the just-created parent. Direct callers create each child through Create (no plugin
            // pipeline); the plugin pipeline supplies its own callback so descendants re-enter the pipeline.
            CreateRelatedEntities(entity, entity.LogicalName, id, Create);
            return id;
        }

        // The single-record create primitive: validate + persist exactly ONE record, without recursing into a
        // deep-insert's RelatedEntities. Shared by the direct path (Create, above) and the plugin pipeline
        // (PluginPipeline.ExecuteCreateMultiple), so a deep insert driven through the pipeline can persist each
        // record here while the pipeline itself drives the child recursion - firing every child's Create steps -
        // instead of the store silently persisting descendants with no plugins. Mirrors RemoveRecordDirect for Delete.
        internal Guid CreateRecordDirect(Entity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            EnsureWritable(entity.LogicalName);
            DemandOwnerColumn(entity);
            DemandCreate(entity);

            // A lookup set by key is resolved first, so the existence and record-scoped AppendTo checks run against
            // the record it names, exactly as for the same lookup set by id.
            Entity resolved = WithResolvedLookupKeys(entity);
            DemandLookupTargetsExist(resolved, onCreate: true);
            DemandLookupAppends(resolved, null);
            DemandSecuredColumnWrites(entity, onCreate: true);

            Entity stored = EntityValues.Clone(resolved);
            if (stored.Id == Guid.Empty)
            {
                stored.Id = _idSequence();
            }
            else if (Table(stored.LogicalName).ContainsKey(stored.Id))
            {
                // Caller supplied an id that already exists: the platform rejects a duplicate primary key.
                throw RecordAlreadyExists(stored.LogicalName, stored.Id);
            }

            _autoNumbers.Apply(stored);
            _systemFields.ApplyOnCreate(entity, stored, StampingActor(), _clock(), NextVersion());
            RealignOwningBusinessUnit(stored);
            _alternateKeys.Enforce(stored, Table(stored.LogicalName).Values);
            Table(stored.LogicalName)[stored.Id] = stored;
            DeriveInheritedAccess(stored);
            return stored.Id;
        }

        // Creates the 1:N children carried in a deep-insert target's RelatedEntities. The child's parent lookup is
        // set from the relationship's ReferencingAttribute, so the relationship must be declared (WithRelationship /
        // WithParentalRelationship / WithReferentialRelationship) - the generated nav property carries only the
        // schema name, not the child foreign-key column. N:N deep associate is not handled here.
        internal void CreateRelatedEntities(Entity parent, string parentEntity, Guid parentId, Func<Entity, Guid> create)
        {
            foreach (KeyValuePair<Relationship, EntityCollection> pair in parent.RelatedEntities)
            {
                EntityCollection collection = pair.Value;
                if (collection?.Entities == null || collection.Entities.Count == 0)
                {
                    continue;
                }

                string childEntity = collection.Entities[0].LogicalName;
                OneToManyRegistry.OneToMany relationship = ResolveOneToManyForDeepInsert(pair.Key.SchemaName, parentEntity, childEntity);
                foreach (Entity child in collection.Entities)
                {
                    // Clone before stamping the parent FK so the deep insert never mutates the caller's child
                    // entities (a real service round-trips copies, leaving the request graph untouched).
                    Entity toCreate = EntityValues.Clone(child);
                    EntityValues.CarryEarlyBoundType(child, toCreate);
                    toCreate[relationship.ReferencingAttribute] = new EntityReference(parentEntity, parentId);

                    // EntityValues.Clone copies only attributes, so carry over the child's own RelatedEntities
                    // to the record being created; Create -> CreateRelatedEntities then recurses into them,
                    // making a multi-level deep insert (grandchildren and deeper) work like the platform. The
                    // nested entities are themselves cloned before being created, so the caller's graph is untouched.
                    foreach (KeyValuePair<Relationship, EntityCollection> nested in child.RelatedEntities)
                    {
                        toCreate.RelatedEntities[nested.Key] = nested.Value;
                    }

                    // The child carries its own RelatedEntities (grandchildren); the callback recurses into them the
                    // same way, so a multi-level deep insert stays intact whether driven directly or via the pipeline.
                    create(toCreate);
                }
            }
        }

        private OneToManyRegistry.OneToMany ResolveOneToManyForDeepInsert(string schemaName, string parentEntity, string childEntity)
        {
            List<OneToManyRegistry.OneToMany> candidates = _oneToMany.ChildRelationshipsOf(parentEntity)
                .Where(r => string.Equals(r.ReferencingEntity, childEntity, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer an exact schema-name match when several relationships link the same pair of tables.
            OneToManyRegistry.OneToMany? bySchema = candidates
                .FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
            if (bySchema != null)
            {
                return bySchema;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Deep insert: no 1:N relationship from '{parentEntity}' to '{childEntity}' (schema '{schemaName}') is declared on the in-memory service. Declare it with WithRelationship/WithParentalRelationship/WithReferentialRelationship.");
            }

            throw new InvalidOperationException(
                $"Deep insert: several 1:N relationships link '{parentEntity}' to '{childEntity}' and none matches schema '{schemaName}'. Pass the schema name to the declaration (WithRelationship(..., schemaName: \"{schemaName}\")) so it can be resolved.");
        }

        // Resolves any lookup value supplied as an alternate-key EntityReference (KeyAttributes set, Id empty) to a
        // concrete-id EntityReference at write time, so the persisted lookup points at a real record and later
        // joins/reads find it - matching Dataverse, which resolves a key-addressed lookup on write. A key that
        // matches no record throws the platform not-found fault (ResolveReference), like relating to a missing record.
        // The entity with every lookup set by key resolved to its record id: the entity itself when it has none, else
        // a copy, so the caller's entity is never changed.
        private Entity WithResolvedLookupKeys(Entity entity)
        {
            if (!entity.Attributes.Any(attribute => attribute.Value is EntityReference reference
                && reference.Id == Guid.Empty
                && reference.KeyAttributes != null
                && reference.KeyAttributes.Count > 0))
            {
                return entity;
            }

            Entity resolved = EntityValues.Clone(entity);
            ResolveLookupKeyReferences(resolved);
            return resolved;
        }

        private void ResolveLookupKeyReferences(Entity entity)
        {
            foreach (string attribute in entity.Attributes.Keys.ToArray())
            {
                if (entity[attribute] is EntityReference reference
                    && reference.Id == Guid.Empty
                    && reference.KeyAttributes != null
                    && reference.KeyAttributes.Count > 0)
                {
                    entity[attribute] = new EntityReference(reference.LogicalName, ResolveReference(reference));
                }
            }
        }

        // Resolves an EntityReference to a record id: a direct id when set, otherwise an alternate-key lookup
        // (KeyAttributes) - the record whose columns match every key attribute. Throws the platform not-found fault
        // when no record matches, so RetrieveByKey and key-based Update/Delete behave like a real service.
        internal Guid ResolveReference(EntityReference reference)
        {
            if (reference.Id != Guid.Empty || reference.KeyAttributes == null || reference.KeyAttributes.Count == 0)
            {
                return reference.Id;
            }

            if (TryResolveReference(reference, out Guid id))
            {
                return id;
            }

            // 0x80060891 RecordNotFoundByEntityKey, the fault the platform raises for a declared key that matches
            // nothing (verified live), not the by-id 0x80040217.
            string keys = string.Join(", ", reference.KeyAttributes.Select(k => $"{k.Key}={k.Value}"));
            string message = $"A record with the specified key values does not exist in {reference.LogicalName} entity ({keys})";
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80060891), Message = message },
                new FaultReason(message));
        }

        // Non-throwing reference resolution: a direct id (when it exists), else an alternate-key match. Returns
        // false when nothing matches (used by upsert to decide create vs update).
        internal bool TryResolveReference(EntityReference reference, out Guid id)
        {
            id = Guid.Empty;
            if (reference.Id != Guid.Empty)
            {
                if (Table(reference.LogicalName).ContainsKey(reference.Id))
                {
                    id = reference.Id;
                    return true;
                }

                return false;
            }

            if (reference.KeyAttributes == null || reference.KeyAttributes.Count == 0)
            {
                return false;
            }

            // Only a declared key addresses a record; any other set of columns is rejected, not matched.
            _alternateKeys.DemandDeclared(reference.LogicalName, reference.KeyAttributes.Keys);

            foreach (Entity candidate in Table(reference.LogicalName).Values)
            {
                if (reference.KeyAttributes.All(k =>
                        candidate.Attributes.TryGetValue(k.Key, out object? value) && KeyValueEquals(value, k.Value)))
                {
                    id = candidate.Id;
                    return true;
                }
            }

            return false;
        }

        // Create-or-update by primary id or alternate key. Matches real Dataverse: a matching record is updated,
        // otherwise one is created; the response reports which happened and the affected reference.
        private UpsertResponse ExecuteUpsert(Entity target)
        {
            EntityReference key = target.Id != Guid.Empty
                ? new EntityReference(target.LogicalName, target.Id)
                : new EntityReference(target.LogicalName) { KeyAttributes = target.KeyAttributes };

            UpsertResponse response = new UpsertResponse();
            if (TryResolveReference(key, out Guid existingId))
            {
                Entity toUpdate = EntityValues.Clone(target);
                EntityValues.CarryEarlyBoundType(target, toUpdate);
                toUpdate.Id = existingId;
                toUpdate.KeyAttributes.Clear();
                Update(toUpdate);
                response.Results["RecordCreated"] = false;
                response.Results["Target"] = new EntityReference(target.LogicalName, existingId);
                return response;
            }

            Entity toCreate = EntityValues.Clone(target);
            EntityValues.CarryEarlyBoundType(target, toCreate);
            // The alternate-key columns are real columns: seed them from the key so the new record carries them.
            foreach (KeyValuePair<string, object> pair in target.KeyAttributes)
            {
                if (!toCreate.Attributes.Contains(pair.Key))
                {
                    toCreate[pair.Key] = pair.Value;
                }
            }

            toCreate.KeyAttributes.Clear();
            Guid newId = Create(toCreate);
            response.Results["RecordCreated"] = true;
            response.Results["Target"] = new EntityReference(target.LogicalName, newId);
            return response;
        }

        // Compares an alternate-key value to a stored attribute value through the shared value comparer, so key
        // RESOLUTION matches key UNIQUENESS (AlternateKeyValidator uses the same comparer). This unwraps the
        // reference types Dataverse uses for key columns and, crucially, compares strings case-insensitively -
        // Dataverse alternate keys are case-insensitive, so upserting "abc" must update a stored "ABC" rather than
        // fault as a missing key.
        private static bool KeyValueEquals(object? stored, object? key)
        {
            return EntityValues.AreEqual(stored, key);
        }

        // Resolves a write target that identifies its row by alternate key (Id empty + KeyAttributes) to a concrete
        // Id, returning a clone with the Id set and the key attributes cleared, so update-by-key works like a real
        // org; a key that matches nothing throws the platform not-found fault. A target with an Id (or no key) is
        // returned unchanged. Every Update path goes through it (UpdateCore), and the pipeline calls it before any
        // step fires, so the steps see Target.Id set to the resolved record, as on Dataverse.
        internal Entity ResolveWriteTarget(Entity target)
        {
            if (target.Id != Guid.Empty || target.KeyAttributes == null || target.KeyAttributes.Count == 0)
            {
                return target;
            }

            Guid id = ResolveReference(new EntityReference(target.LogicalName) { KeyAttributes = target.KeyAttributes });
            Entity resolved = EntityValues.Clone(target);
            EntityValues.CarryEarlyBoundType(target, resolved);
            resolved.Id = id;
            resolved.KeyAttributes.Clear();
            return resolved;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            if (!Table(entityName).TryGetValue(id, out Entity? stored))
            {
                throw RecordDoesNotExist(entityName, id);
            }

            DemandRecordRead(entityName, stored);

            Entity projected = Project(stored, columnSet);
            MaskSecuredColumns(projected);
            ShapeRead(projected);
            return projected;
        }

        public void Update(Entity entity) => UpdateCore(entity);

        // Shared by Update and Assign (assignment is an ownerid update): the required privilege follows the
        // content — Write for field changes, Assign for an ownership change — so both message paths enforce the
        // same rules (mirroring Dataverse, where setting ownerid needs prvAssign, not Write).
        private void UpdateCore(Entity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            EnsureWritable(entity.LogicalName);

            // A row addressed by alternate key is updated like one addressed by id (Update, UpdateMultiple, Upsert).
            entity = ResolveWriteTarget(entity);
            if (!Table(entity.LogicalName).TryGetValue(entity.Id, out Entity? stored))
            {
                throw RecordDoesNotExist(entity.LogicalName, entity.Id);
            }

            DemandOwnerColumn(entity);
            bool changesOwner = entity.Contains(SystemFieldApplier.OwnerId);
            if (changesOwner && entity[SystemFieldApplier.OwnerId] == null)
            {
                // Verified live: the platform rejects clearing the owner; it does not reassign the record.
                throw PlatformFault.Create(PlatformFault.InvalidArgument, $"Attribute: {SystemFieldApplier.OwnerId} cannot be set to NULL");
            }

            // Captured before the merge: needed for the UserOwned cascade scope (children owned by the same user).
            EntityReference? previousOwner = changesOwner ? stored.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) : null;
            if (entity.Attributes.Keys.Any(key => !string.Equals(key, SystemFieldApplier.OwnerId, StringComparison.OrdinalIgnoreCase)))
            {
                DemandAccess(entity.LogicalName, Privilege.Write, stored);
            }

            if (changesOwner)
            {
                DemandAccess(entity.LogicalName, Privilege.Assign, stored);
                DemandOwnerCanRead(entity.LogicalName, entity[SystemFieldApplier.OwnerId] as EntityReference, onCreate: false);
            }

            // A lookup set by key is resolved first (a key that matches nothing throws before the stored row
            // changes), so the existence and record-scoped AppendTo checks run against the record it names.
            Entity resolved = WithResolvedLookupKeys(entity);
            DemandLookupTargetsExist(resolved, onCreate: false);
            DemandLookupAppends(resolved, stored);
            DemandSecuredColumnWrites(entity, onCreate: false);

            // Dataverse Update merges the supplied attributes onto the existing record. The merge is built on a copy
            // (deep-copying the incoming values, so the caller's entity does not stay aliased with the store) and
            // validated there: a lookup key that matches nothing or a duplicate alternate key throws before the
            // stored row changes.
            Entity candidate = EntityValues.Clone(stored);
            foreach (KeyValuePair<string, object> attribute in resolved.Attributes)
            {
                if (!IgnoredOnUpdate.Contains(attribute.Key))
                {
                    candidate[attribute.Key] = EntityValues.CloneValue(attribute.Value);
                }
            }

            // A state change without a status lands on the state's default status (the platform sets it on the
            // record; the caller's Target is left as sent).
            if (entity.GetAttributeValue<OptionSetValue>(SystemFieldApplier.StateCode) is OptionSetValue state
                && !entity.Contains(SystemFieldApplier.StatusCode))
            {
                candidate[SystemFieldApplier.StatusCode] = new OptionSetValue(DefaultStatus(entity.LogicalName, state.Value));
            }

            _alternateKeys.Enforce(candidate, Table(entity.LogicalName).Values);

            Dictionary<string, EntityReference?> previousParents = ReparentLookups(stored);
            foreach (KeyValuePair<string, object> attribute in candidate.Attributes)
            {
                stored[attribute.Key] = attribute.Value;
            }

            DeriveInheritedAccessIfReparented(stored, previousParents);

            _systemFields.ApplyOnUpdate(entity, stored, StampingActor(), _clock(), NextVersion());
            if (changesOwner)
            {
                RealignOwningBusinessUnit(stored);
                EntityReference? newOwner = stored.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId);
                if (newOwner != null)
                {
                    CascadeAssign(entity.LogicalName, entity.Id, previousOwner, newOwner, new HashSet<Guid>());
                }
            }
        }

        // The statecode/statuscode Update a SetState request performs (status -1 or missing = the state's default).
        // Server-managed columns an Update silently ignores (verified live): the creation audit columns and
        // owningbusinessunit, which follows the owner. modifiedon/modifiedby are restamped by ApplyOnUpdate.
        private static readonly HashSet<string> IgnoredOnUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SystemFieldApplier.CreatedOn,
            SystemFieldApplier.CreatedBy,
            SystemFieldApplier.OwningBusinessUnit,
        };

        // An organization-owned table (declared, or by its registered metadata) has no ownerid column: a Create or
        // Update that sets it faults (verified live).
        private void DemandOwnerColumn(Entity entity)
        {
            if (entity.Contains(SystemFieldApplier.OwnerId) && _systemFields.IsKnownOrganizationOwned(entity.LogicalName))
            {
                throw PlatformFault.Create(PlatformFault.NoSuchAttribute,
                    $"'{entity.LogicalName}' entity doesn't contain attribute with Name = '{SystemFieldApplier.OwnerId}' and NameMapping = 'Logical' (look up attribute by name is case-sensitive).");
            }
        }

        private long NextVersion() => ++_version;

        private void EnsureWritable(string logicalName)
        {
            if (_readOnlyTables.Contains(logicalName))
            {
                throw new InvalidOperationException(
                    $"'{logicalName}' is a read-only virtual table; writes are rejected (declared with AsVirtual(\"{logicalName}\", readOnly: true)).");
            }
        }

        public void Delete(string entityName, Guid id) => DeleteInternal(entityName, id, RemoveRecordDirect);

        // Delete driven by a caller-supplied per-record remover: direct callers remove the row straight from the
        // store, while the pipeline passes a remover that fires each record's Delete plugins. The store still owns
        // the cascade TRAVERSAL, so there is one cascade implementation regardless of who removes the rows.
        internal void DeleteInternal(string entityName, Guid id, Action<string, Guid> removeRecord)
        {
            EnsureWritable(entityName);
            if (!Table(entityName).TryGetValue(id, out Entity? stored))
            {
                // Deleting a non-existent record is a not-found fault on the platform, not a silent no-op.
                throw RecordDoesNotExist(entityName, id);
            }

            // Unconditional, like Create/Update: with security off this is the WithStrictSecurity guard.
            DemandAccess(entityName, Privilege.Delete, stored);

            // One transaction: a Restrict met deep in the cascade (a grandchild) fails the delete after siblings were
            // already removed or unlinked, so the whole store rolls back to how it was before the delete.
            RunAtomically<object?>(() =>
            {
                CascadeDelete(entityName, id, new HashSet<Guid>(), removeRecord);
                return null;
            });
        }

        // Removes a single record from the store with no cascade — the default remover, and the primitive the
        // pipeline's remover ends with after firing the record's Delete plugins. The record's file blobs go with it,
        // so a stale file id is unknown afterwards.
        internal void RemoveRecordDirect(string entityName, Guid id)
        {
            Table(entityName).Remove(id);
            foreach (Guid fileId in _files
                .Where(file => file.Value.RecordId == id && string.Equals(file.Value.EntityLogicalName, entityName, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Key)
                .ToList())
            {
                _files.Remove(fileId);
            }
        }

        internal bool TryGet(string entityName, Guid id, out Entity? entity)
        {
            if (Table(entityName).TryGetValue(id, out Entity? stored))
            {
                entity = EntityValues.Clone(stored);
                return true;
            }

            entity = null;
            return false;
        }
    }
}
