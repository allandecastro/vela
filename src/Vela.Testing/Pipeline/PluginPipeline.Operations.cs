using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Steps;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // The data operations the pipeline simulates (Create/Update/Delete/Associate/SetState/Upsert and the bulk
    // messages): each fires its stages around the store write inside RunOperation.
    public sealed partial class PluginPipeline
    {
        // A single Create is the one-row case of a Create batch. Routing it through the batch path means
        // CreateMultiple-registered steps also fire for a single Create (Dataverse merges the two messages).
        internal Guid ExecuteCreate(Entity entity, ParameterCollection? sharedVariables = null)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            return ExecuteCreateMultiple(new[] { entity }, sharedVariables)[0];
        }

        // A single Update is the one-row case of an Update batch (see ExecuteCreate). It is ONE execution even when
        // it mixes owner or state fields with standard ones (verified live: one Update per stage, with every field in
        // its Target, contrary to Microsoft Learn's "Behavior of specialized update operations").
        internal void ExecuteUpdate(Entity entity, ParameterCollection? sharedVariables = null)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            ExecuteUpdateMultiple(new[] { entity }, sharedVariables);
        }

        // A write target addressed by alternate key (Id empty + KeyAttributes) is resolved to the record's id before
        // any step fires, as Dataverse does: the steps see Target.Id / PrimaryEntityId set to the resolved record, and
        // a key that matches nothing throws the platform not-found fault. Returns a clone (the caller's entity is not
        // mutated); a target that already has an id (or no key) is returned unchanged.
        private Entity ResolveKeyedTarget(Entity entity)
        {
            if (entity.Id != Guid.Empty || entity.KeyAttributes == null || entity.KeyAttributes.Count == 0)
            {
                return entity;
            }

            Guid id = _store.ResolveReference(new EntityReference(entity.LogicalName) { KeyAttributes = entity.KeyAttributes });
            Entity resolved = EntityValues.Clone(entity);
            resolved.Id = id;
            resolved.KeyAttributes.Clear();
            return resolved;
        }

        // SetState, as the platform runs it (verified live): the request fires the SetStateDynamicEntity steps (never
        // the SetState ones), and its main operation is an Update of statecode/statuscode at the same depth that fires
        // the Update steps at PreOperation and PostOperation only. The SetStateDynamicEntity steps see the Status as
        // sent (-1 = default); the Update Target carries the resolved status (see WithDefaultStatus). The Update is
        // built from the State and Status inputs as the pre stages left them, so a pre step that changes either one
        // changes the state written.
        internal void ExecuteSetState(EntityReference target, OptionSetValue? state, OptionSetValue? status, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(asyncQueue =>
            {
                EntityReference record = new EntityReference(target.LogicalName, _store.ResolveReference(target));
                FireSetStateDynamicEntity(
                    record,
                    state ?? new OptionSetValue(0),
                    status ?? new OptionSetValue(-1),
                    inputs =>
                    {
                        Entity update = _store.SetStateUpdate(record, OptionSetInput(inputs, "State"), OptionSetInput(inputs, "Status"));
                        UpdateBatch(new[] { update }, platformUpdate: true, asyncQueue);
                    },
                    asyncQueue);
                return null;
            }, sharedVariables);
        }

        private static OptionSetValue? OptionSetInput(ParameterCollection inputs, string name)
            => inputs.TryGetValue(name, out object? value) ? value as OptionSetValue : null;

        // Assign, as the platform runs it (verified live): Assign PreValidation and PreOperation, then the main
        // operation, an ownerid Update at the same depth firing the Update steps at PreOperation and PostOperation
        // only (their ParentContext is the Assign main operation), then Assign PostOperation. (An Update of ownerid,
        // conversely, never fires the Assign steps.) The steps work on a copy of the request, and the owner written is
        // the Assignee as the pre stages left it.
        internal void ExecuteAssign(AssignRequest request, ParameterCollection? sharedVariables = null)
        {
            OrganizationRequest inputs = CopyRequest(request);
            RunOperation<object?>(asyncQueue =>
            {
                EntityReference record = new EntityReference(request.Target.LogicalName, _store.ResolveReference(request.Target));
                _store.TryGet(record.LogicalName, record.Id, out Entity? pre);

                foreach (PluginStages stage in new[] { PluginStages.PreValidation, PluginStages.PreOperation })
                {
                    StageInvocation invocation = GenericInvocation(PluginMessages.Assign, stage, record.LogicalName, record.Id, inputs.Parameters, null);
                    invocation.PreState = pre;
                    FireStage(invocation, asyncQueue);
                    if (stage == PluginStages.PreValidation)
                    {
                        EnterTransaction();
                    }
                }

                EntityReference assignee = inputs.Parameters.TryGetValue("Assignee", out object? value) && value is EntityReference reference
                    ? reference
                    : throw new InvalidPluginExecutionException("Assign: a pre-stage step left InputParameters[\"Assignee\"] without an EntityReference.");
                MarkPlatformUpdate(PluginMessages.Assign, inputs.Parameters);
                UpdateBatch(new[] { new Entity(record.LogicalName, record.Id) { ["ownerid"] = assignee } }, platformUpdate: true, asyncQueue);

                _store.TryGet(record.LogicalName, record.Id, out Entity? post);
                StageInvocation postInvocation = GenericInvocation(PluginMessages.Assign, PluginStages.PostOperation, record.LogicalName, record.Id, inputs.Parameters, null);
                postInvocation.PreState = pre;
                postInvocation.PostState = post;
                FireStage(postInvocation, asyncQueue);
                return null;
            }, sharedVariables);
        }

        // Fires the SetStateDynamicEntity steps around a state change: PreValidation and PreOperation, the main
        // operation (which writes the state, given the inputs as the pre stages left them), then PostOperation.
        // Images come from the record before and after it.
        private void FireSetStateDynamicEntity(EntityReference record, OptionSetValue state, OptionSetValue status, Action<ParameterCollection> mainOperation, List<Action> asyncQueue)
        {
            ParameterCollection inputs = new ParameterCollection
            {
                ["EntityMoniker"] = record,
                ["State"] = state,
                ["Status"] = status,
            };
            _store.TryGet(record.LogicalName, record.Id, out Entity? pre);

            foreach (PluginStages stage in new[] { PluginStages.PreValidation, PluginStages.PreOperation })
            {
                StageInvocation invocation = GenericInvocation(PluginMessages.SetStateDynamicEntity, stage, record.LogicalName, record.Id, inputs, null);
                invocation.PreState = pre;
                FireStage(invocation, asyncQueue);
                if (stage == PluginStages.PreValidation)
                {
                    EnterTransaction();
                }
            }

            MarkPlatformUpdate(PluginMessages.SetStateDynamicEntity, inputs);
            mainOperation(inputs);

            _store.TryGet(record.LogicalName, record.Id, out Entity? post);
            StageInvocation postInvocation = GenericInvocation(PluginMessages.SetStateDynamicEntity, PluginStages.PostOperation, record.LogicalName, record.Id, inputs, null);
            postInvocation.PreState = pre;
            postInvocation.PostState = post;
            FireStage(postInvocation, asyncQueue);
        }

        // Records that the current operation's main operation is a platform Update: the Update steps it fires get
        // this message's stage-30 context (with these inputs) as their ParentContext, not an Update one (verified live
        // for Assign and Merge; SetState follows Assign until probed).
        private void MarkPlatformUpdate(string message, ParameterCollection inputs)
        {
            OperationFrame frame = _frames.Peek();
            frame.MainMessage = message;
            frame.MainInputs = inputs;
        }

        // The main operation of an Update row. A row that changes statecode runs the state change as the platform
        // does (verified live): the standard fields are written first, then the state, inside a nested
        // SetStateDynamicEntity operation (its steps at depth + 1, Status -1 when the row sets no statuscode), then
        // the owner. A platform-generated Update (inside Assign / SetState) is a plain write.
        private void WriteUpdateRow(Entity row, bool platformUpdate, List<Action> asyncQueue)
        {
            if (platformUpdate || row.GetAttributeValue<OptionSetValue>("statecode") is not OptionSetValue state)
            {
                _store.Update(row);
                return;
            }

            Entity standard = new Entity(row.LogicalName, row.Id);
            Entity stateChange = new Entity(row.LogicalName, row.Id);
            Entity owner = new Entity(row.LogicalName, row.Id);
            foreach (KeyValuePair<string, object> attribute in row.Attributes)
            {
                Entity part = attribute.Key.Equals("statecode", StringComparison.OrdinalIgnoreCase) || attribute.Key.Equals("statuscode", StringComparison.OrdinalIgnoreCase)
                    ? stateChange
                    : attribute.Key.Equals("ownerid", StringComparison.OrdinalIgnoreCase) ? owner : standard;
                part[attribute.Key] = attribute.Value;
            }

            if (standard.Attributes.Count > 0)
            {
                _store.Update(standard);
            }

            RunOperation<object?>(nestedQueue =>
            {
                FireSetStateDynamicEntity(
                    new EntityReference(row.LogicalName, row.Id),
                    state,
                    row.GetAttributeValue<OptionSetValue>("statuscode") ?? new OptionSetValue(-1),
                    _ => _store.Update(stateChange),
                    nestedQueue);
                return null;
            });

            if (owner.Attributes.Count > 0)
            {
                _store.Update(owner);
            }
        }

        // CreateMultiple as one transaction: at each stage both the bulk CreateMultiple steps (once, with the
        // whole Targets collection) and the single Create steps (once per row) fire, mirroring how Dataverse
        // merges the two messages. All rows commit between the pre and post stages.
        internal Guid[] ExecuteCreateMultiple(Entity[] entities, ParameterCollection? sharedVariables = null)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            // The pipeline works on copies: the caller's entities keep their state (no id is written back to them).
            entities = entities.Select(EntityValues.CloneTarget).ToArray();
            return RunOperation(asyncQueue =>
            {
                string entityName = entities.Length > 0 ? entities[0].LogicalName : string.Empty;
                // FilterOn applies to Create as it does to Update: a Create step fires only when the request sets one
                // of its filtering attributes, so the "changed" set of a create is the attributes it carries.
                List<BatchItem> items = entities.Select(e => new BatchItem
                {
                    Target = e,
                    ChangedAttributes = new HashSet<string>(e.Attributes.Keys, StringComparer.OrdinalIgnoreCase)
                }).ToList();

                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreValidation, entityName, items, asyncQueue);
                EnterTransaction();
                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreOperation, entityName, items, asyncQueue);

                Guid[] ids = new Guid[entities.Length];
                for (int i = 0; i < entities.Length; i++)
                {
                    // Persist just this record (no deep-insert recursion in the store), then drive the deep insert's
                    // children back through the pipeline so each child fires its own Create steps + images (and, via
                    // the same callback, its grandchildren) - instead of the store persisting descendants with no
                    // plugins. CreateRecordDirect avoids double-persisting the descendants the recursion creates.
                    ids[i] = _store.CreateRecordDirect(entities[i]);
                    entities[i].Id = ids[i]; // the post-operation Target carries the created id
                    _store.CreateRelatedEntities(entities[i], entities[i].LogicalName, ids[i], child => ExecuteCreate(child));
                    _store.TryGet(entities[i].LogicalName, ids[i], out Entity? post);
                    items[i].PostState = post;
                }

                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PostOperation, entityName, items, asyncQueue);
                return ids;
            }, sharedVariables);
        }

        // UpdateMultiple as one transaction (see ExecuteCreateMultiple). Single Update steps fire per row only
        // for rows whose changed attributes match the step filter; a bulk UpdateMultiple step fires once if any
        // row matches.
        internal void ExecuteUpdateMultiple(Entity[] entities, ParameterCollection? sharedVariables = null)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            entities = entities.Select(entity => ResolveKeyedTarget(EntityValues.CloneTarget(entity))).ToArray();
            RunOperation<object?>(asyncQueue =>
            {
                UpdateBatch(entities, platformUpdate: false, asyncQueue);
                return null;
            }, sharedVariables);
        }

        // The stages of an Update batch inside the current operation. A platform-generated Update (the main operation
        // of Assign or SetState) has no PreValidation stage (verified live) and runs at its parent's depth.
        private void UpdateBatch(Entity[] entities, bool platformUpdate, List<Action> asyncQueue)
        {
            string entityName = entities.Length > 0 ? entities[0].LogicalName : string.Empty;
            List<BatchItem> items = new List<BatchItem>(entities.Length);
            foreach (Entity entity in entities)
            {
                _store.TryGet(entity.LogicalName, entity.Id, out Entity? pre);
                items.Add(new BatchItem
                {
                    Target = entity,
                    PreState = pre,
                    ChangedAttributes = new HashSet<string>(entity.Attributes.Keys, StringComparer.OrdinalIgnoreCase)
                });
            }

            if (!platformUpdate)
            {
                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreValidation, entityName, items, asyncQueue);
            }

            EnterTransaction();

            FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreOperation, entityName, items, asyncQueue);

            foreach (Entity entity in entities)
            {
                WriteUpdateRow(entity, platformUpdate, asyncQueue);
            }

            foreach (BatchItem item in items)
            {
                _store.TryGet(item.Target.LogicalName, item.Target.Id, out Entity? post);
                item.PostState = post;
            }

            FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PostOperation, entityName, items, asyncQueue);
        }

        internal void ExecuteDelete(string entityName, Guid id, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(asyncQueue =>
            {
                // The deleted record's PreValidation stage runs first, before the transaction opens and before the
                // privilege check and the cascade (as on the platform); a record that does not exist skips it and
                // fails with the store's not-found fault.
                bool rootValidated = false;
                if (_store.TryGet(entityName, id, out Entity? rootPre))
                {
                    FireStage(DeleteInvocation(PluginStages.PreValidation, entityName, id, rootPre), asyncQueue);
                    rootValidated = true;
                }

                EnterTransaction();

                // The store owns the cascade traversal (Restrict/RemoveLink/recursion) but delegates each actual
                // record removal to us, so the Delete plugins fire for the parent AND every cascaded child.
                _store.DeleteInternal(entityName, id, (name, recordId) =>
                    FireDeleteAndRemove(name, recordId, asyncQueue, skipPreValidation: rootValidated && recordId == id));
                return null;
            }, sharedVariables);
        }

        private static StageInvocation DeleteInvocation(PluginStages stage, string entityName, Guid id, Entity? pre)
        {
            StageInvocation invocation = NewInvocation(PluginMessages.Delete, stage, entityName, id, new EntityReference(entityName, id));
            invocation.PreState = pre;
            return invocation;
        }

        // Fires one record's Delete pre-stages, removes just that record (the store handles cascade around this),
        // then fires its post-stages — the same pre/remove/post a top-level Delete runs, applied per cascaded record.
        private void FireDeleteAndRemove(string entityName, Guid id, List<Action> asyncQueue, bool skipPreValidation)
        {
            _store.TryGet(entityName, id, out Entity? pre);

            StageInvocation Make(PluginStages stage) => DeleteInvocation(stage, entityName, id, pre);

            if (!skipPreValidation)
            {
                FireStage(Make(PluginStages.PreValidation), asyncQueue);
            }

            FireStage(Make(PluginStages.PreOperation), asyncQueue);
            _store.RemoveRecordDirect(entityName, id);
            FireStage(Make(PluginStages.PostOperation), asyncQueue);
        }

        internal void ExecuteAssociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, ParameterCollection? sharedVariables = null)
        {
            RunAssociation(PluginMessages.Associate, entityName, entityId, relationship, relatedEntities, sharedVariables);
        }

        internal void ExecuteDisassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, ParameterCollection? sharedVariables = null)
        {
            RunAssociation(PluginMessages.Disassociate, entityName, entityId, relationship, relatedEntities, sharedVariables);
        }

        // Upsert, mirroring Dataverse: the target is matched by its id or alternate key. A match runs the Update
        // pipeline (Update steps fire with the resolved id); no match runs the Create pipeline (Create steps fire; the
        // key columns become real columns of the new row, and a supplied id is kept). Steps registered on the Upsert
        // message itself fire around the inner operation, and the response carries RecordCreated and Target.
        private OrganizationResponse ExecuteUpsert(OrganizationRequest request, Entity callerTarget, ParameterCollection shared)
        {
            // The steps work on a copy of the request (the caller's entity is never touched), and the main operation
            // reads the Target the pre stages left, so a step that changes or replaces it is honoured.
            OrganizationRequest inputs = CopyRequest(request);
            return RunOperation(asyncQueue =>
            {
                FireStage(GenericInvocation("Upsert", PluginStages.PreValidation, callerTarget.LogicalName, callerTarget.Id, inputs.Parameters, null), asyncQueue);
                EnterTransaction();
                FireStage(GenericInvocation("Upsert", PluginStages.PreOperation, callerTarget.LogicalName, callerTarget.Id, inputs.Parameters, null), asyncQueue);

                Entity target = inputs.Parameters.TryGetValue("Target", out object? effective) && effective is Entity replaced
                    ? replaced
                    : throw new InvalidPluginExecutionException("Upsert: a pre-stage step left InputParameters[\"Target\"] without an entity.");

                EntityReference key = target.Id != Guid.Empty
                    ? new EntityReference(target.LogicalName, target.Id)
                    : new EntityReference(target.LogicalName) { KeyAttributes = target.KeyAttributes };

                UpsertResponse response = new UpsertResponse();
                Guid affectedId;
                if (_store.TryResolveReference(key, out Guid existingId))
                {
                    Entity toUpdate = EntityValues.Clone(target);
                    toUpdate.Id = existingId;
                    toUpdate.KeyAttributes.Clear();
                    ExecuteUpdate(toUpdate);
                    response.Results["RecordCreated"] = false;
                    affectedId = existingId;
                }
                else
                {
                    Entity toCreate = EntityValues.Clone(target);
                    foreach (KeyValuePair<string, object> pair in target.KeyAttributes)
                    {
                        if (!toCreate.Attributes.Contains(pair.Key))
                        {
                            toCreate[pair.Key] = pair.Value;
                        }
                    }

                    toCreate.KeyAttributes.Clear();
                    affectedId = ExecuteCreate(toCreate);
                    response.Results["RecordCreated"] = true;
                }

                response.Results["Target"] = new EntityReference(target.LogicalName, affectedId);
                FireStage(GenericInvocation("Upsert", PluginStages.PostOperation, target.LogicalName, affectedId, inputs.Parameters, response.Results), asyncQueue);
                return (OrganizationResponse)response;
            }, shared);
        }

        private void RunAssociation(string message, string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(asyncQueue =>
            {
                Dictionary<string, object> extra = new Dictionary<string, object>
                {
                    ["Relationship"] = relationship,
                    ["RelatedEntities"] = relatedEntities
                };
                EntityReference target = new EntityReference(entityName, entityId);

                StageInvocation Make(PluginStages stage)
                {
                    StageInvocation invocation = NewInvocation(message, stage, entityName, entityId, target);
                    invocation.ExtraInputs = extra;
                    return invocation;
                }

                FireStage(Make(PluginStages.PreValidation), asyncQueue);
                EnterTransaction();
                FireStage(Make(PluginStages.PreOperation), asyncQueue);

                // Persist the N:N change so the post stage (and later reads) see the intersect rows. The
                // surrounding snapshot/rollback (RunOperation) undoes this if a synchronous plugin throws.
                if (message == PluginMessages.Associate)
                {
                    _store.Associate(entityName, entityId, relationship, relatedEntities);
                }
                else
                {
                    _store.Disassociate(entityName, entityId, relationship, relatedEntities);
                }

                FireStage(Make(PluginStages.PostOperation), asyncQueue);

                return null;
            }, sharedVariables);
        }
    }
}
