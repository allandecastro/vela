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
        internal Guid ExecuteCreate(Entity entity, ParameterCollection? sharedVariables = null, FakePluginExecutionContext? innerOf = null)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            return ExecuteCreateMultiple(new[] { entity }, sharedVariables, bulkRequest: false, innerOf)[0];
        }

        // A single Update is the one-row case of an Update batch (see ExecuteCreate). It is ONE execution even when
        // it mixes owner or state fields with standard ones (verified live: one Update per stage, with every field in
        // its Target, contrary to Microsoft Learn's "Behavior of specialized update operations").
        internal void ExecuteUpdate(Entity entity, ParameterCollection? sharedVariables = null, FakePluginExecutionContext? innerOf = null)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            ExecuteUpdateMultiple(new[] { entity }, sharedVariables, bulkRequest: false, innerOf);
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
            _store.DemandAssignable(request.Target.LogicalName);
            OrganizationRequest inputs = CopyRequest(request);
            RunOperation<object?>(asyncQueue =>
            {
                EntityReference record = new EntityReference(request.Target.LogicalName, _store.ResolveReference(request.Target));
                FireRecordStages(PluginMessages.Assign, record, inputs.Parameters, () =>
                {
                    EntityReference assignee = inputs.Parameters.TryGetValue("Assignee", out object? value) && value is EntityReference reference
                        ? reference
                        : throw new InvalidPluginExecutionException("Assign: a pre-stage step left InputParameters[\"Assignee\"] without an EntityReference.");
                    MarkPlatformUpdate(PluginMessages.Assign, inputs.Parameters);
                    UpdateBatch(new[] { new Entity(record.LogicalName, record.Id) { ["ownerid"] = assignee } }, platformUpdate: true, asyncQueue);
                }, asyncQueue);
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

            FireRecordStages(PluginMessages.SetStateDynamicEntity, record, inputs, () =>
            {
                MarkPlatformUpdate(PluginMessages.SetStateDynamicEntity, inputs);
                mainOperation(inputs);
            }, asyncQueue);
        }

        // The stages of a message on one record (Assign, SetStateDynamicEntity) around its main operation: every stage
        // gets the record before the operation as its pre-image state, and PostOperation the record after it.
        private void FireRecordStages(string message, EntityReference record, ParameterCollection inputs, Action mainOperation, List<Action> asyncQueue)
        {
            _store.TryGet(record.LogicalName, record.Id, out Entity? pre);
            RunStages<object?>(
                stage =>
                {
                    StageInvocation invocation = GenericInvocation(message, stage, record.LogicalName, record.Id, inputs, null);
                    invocation.PreState = pre;
                    FireStage(invocation, asyncQueue);
                },
                () =>
                {
                    mainOperation();
                    return null;
                },
                _ =>
                {
                    _store.TryGet(record.LogicalName, record.Id, out Entity? post);
                    StageInvocation postInvocation = GenericInvocation(message, PluginStages.PostOperation, record.LogicalName, record.Id, inputs, null);
                    postInvocation.PreState = pre;
                    postInvocation.PostState = post;
                    FireStage(postInvocation, asyncQueue);
                });
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
        // merges the two messages. All rows commit between the pre and post stages. bulkRequest: the request was a
        // CreateMultiple (not a single Create), which changes the rows' SharedVariables and ParentContext.
        internal Guid[] ExecuteCreateMultiple(Entity[] entities, ParameterCollection? sharedVariables = null, bool bulkRequest = true, FakePluginExecutionContext? innerOf = null)
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

                return RunStages(
                    stage => FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, stage, entityName, items, asyncQueue),
                    () =>
                    {
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

                        return ids;
                    },
                    _ => FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PostOperation, entityName, items, asyncQueue));
            }, sharedVariables, bulkRequest ? PluginMessages.CreateMultiple : null, innerOf);
        }

        // UpdateMultiple as one transaction (see ExecuteCreateMultiple). Single Update steps fire per row only
        // for rows whose changed attributes match the step filter; a bulk UpdateMultiple step fires once if any
        // row matches.
        internal void ExecuteUpdateMultiple(Entity[] entities, ParameterCollection? sharedVariables = null, bool bulkRequest = true, FakePluginExecutionContext? innerOf = null)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            // A target addressed by alternate key is resolved to the record's id before any step fires, as Dataverse
            // does: the steps see Target.Id / PrimaryEntityId set to the resolved record.
            entities = entities.Select(entity => _store.ResolveWriteTarget(EntityValues.CloneTarget(entity))).ToArray();
            RunOperation<object?>(asyncQueue =>
            {
                UpdateBatch(entities, platformUpdate: false, asyncQueue);
                return null;
            }, sharedVariables, bulkRequest ? PluginMessages.UpdateMultiple : null, innerOf);
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

            RunStages<object?>(
                stage => FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, stage, entityName, items, asyncQueue),
                () =>
                {
                    foreach (Entity entity in entities)
                    {
                        WriteUpdateRow(entity, platformUpdate, asyncQueue);
                    }

                    foreach (BatchItem item in items)
                    {
                        _store.TryGet(item.Target.LogicalName, item.Target.Id, out Entity? post);
                        item.PostState = post;
                    }

                    return null;
                },
                _ => FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PostOperation, entityName, items, asyncQueue),
                preValidation: !platformUpdate);
        }

        internal void ExecuteDelete(string entityName, Guid id, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(asyncQueue =>
            {
                // The deleted record's PreValidation stage runs first, before the transaction opens and before the
                // privilege check and the cascade (as on the platform); a record that does not exist skips it and
                // fails with the store's not-found fault.
                if (_store.TryGet(entityName, id, out Entity? rootPre))
                {
                    FireStage(DeleteInvocation(PluginStages.PreValidation, entityName, id, rootPre), asyncQueue);
                }

                EnterTransaction();

                // The root's stage-30 context exists before the cascade: the children's steps run first and have it as
                // their ParentContext (verified live).
                EntityReference rootReference = new EntityReference(entityName, id);
                MainStageContext(_frames.Peek(), PluginMessages.Delete, entityName, id, () => new ParameterCollection { ["Target"] = rootReference });

                // The store owns the cascade traversal (Restrict/RemoveLink/recursion) but delegates each actual
                // record removal to us, so the Delete plugins fire for the parent AND every cascaded child.
                _store.DeleteInternal(entityName, id, (name, recordId) =>
                    FireDeleteAndRemove(name, recordId, asyncQueue, cascaded: !(recordId == id && string.Equals(name, entityName, StringComparison.OrdinalIgnoreCase))));
                return null;
            }, sharedVariables);
        }

        private static StageInvocation DeleteInvocation(PluginStages stage, string entityName, Guid id, Entity? pre)
        {
            StageInvocation invocation = NewInvocation(PluginMessages.Delete, stage, entityName, id, new EntityReference(entityName, id));
            invocation.PreState = pre;
            return invocation;
        }

        // Fires one record's Delete PreOperation stage, removes just that record (the store handles the cascade around
        // this), then fires its PostOperation stage. A cascaded child's Delete, as the platform runs it (verified live):
        // no PreValidation stage, the root's depth and stage-30 context (ParentContext), and its own SharedVariables
        // shared by its PreOperation and PostOperation steps.
        private void FireDeleteAndRemove(string entityName, Guid id, List<Action> asyncQueue, bool cascaded)
        {
            _store.TryGet(entityName, id, out Entity? pre);
            ParameterCollection? childShared = cascaded ? new ParameterCollection() : null;

            StageInvocation Make(PluginStages stage)
            {
                StageInvocation invocation = DeleteInvocation(stage, entityName, id, pre);
                invocation.SharedVariables = childShared;
                return invocation;
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
        // key columns become real columns of the new row, and a supplied id is kept). The inner Create or Update is the
        // Upsert's main operation, at its depth (verified live). Steps registered on the Upsert message itself fire
        // around it, and the response carries RecordCreated and Target.
        private OrganizationResponse ExecuteUpsert(OrganizationRequest request, Entity callerTarget, ParameterCollection shared)
        {
            // The steps work on a copy of the request (the caller's entity is never touched), and the main operation
            // reads the Target the pre stages left, so a step that changes or replaces it is honoured.
            OrganizationRequest inputs = CopyRequest(request);
            return RunOperation(asyncQueue => RunStages<OrganizationResponse>(
                stage => FireStage(GenericInvocation("Upsert", stage, callerTarget.LogicalName, callerTarget.Id, inputs.Parameters, null), asyncQueue),
                () => ExecuteUpsertMain(inputs),
                response =>
                {
                    EntityReference affected = (EntityReference)response.Results["Target"];
                    FireStage(GenericInvocation("Upsert", PluginStages.PostOperation, affected.LogicalName, affected.Id, inputs.Parameters, response.Results), asyncQueue);
                }),
                shared);
        }

        // The Upsert's main operation: the inner Update (a match) or Create (no match) of the Target the pre stages left.
        private UpsertResponse ExecuteUpsertMain(OrganizationRequest inputs)
        {
            Entity target = inputs.Parameters.TryGetValue("Target", out object? effective) && effective is Entity replaced
                ? replaced
                : throw new InvalidPluginExecutionException("Upsert: a pre-stage step left InputParameters[\"Target\"] without an entity.");

            EntityReference key = target.Id != Guid.Empty
                ? new EntityReference(target.LogicalName, target.Id)
                : new EntityReference(target.LogicalName) { KeyAttributes = target.KeyAttributes };

            // The inner Create or Update runs at the Upsert's depth, its own stages and SharedVariables included,
            // with the Upsert main operation (PrimaryEntityName "none") as its ParentContext (verified live).
            FakePluginExecutionContext upsertMain = PlatformContext(
                "Upsert",
                PluginStages.MainOperation,
                "none",
                Guid.Empty,
                _sharedVariables,
                _contextStack.Count > 0 ? _contextStack.Peek() : null,
                inputs.Parameters);

            UpsertResponse response = new UpsertResponse();
            Guid affectedId;
            if (_store.TryResolveReference(key, out Guid existingId))
            {
                Entity toUpdate = EntityValues.Clone(target);
                EntityValues.CarryEarlyBoundType(target, toUpdate);
                toUpdate.Id = existingId;
                toUpdate.KeyAttributes.Clear();
                ExecuteUpdate(toUpdate, innerOf: upsertMain);
                response.Results["RecordCreated"] = false;
                affectedId = existingId;
            }
            else
            {
                Entity toCreate = EntityValues.Clone(target);
                EntityValues.CarryEarlyBoundType(target, toCreate);
                foreach (KeyValuePair<string, object> pair in target.KeyAttributes)
                {
                    if (!toCreate.Attributes.Contains(pair.Key))
                    {
                        toCreate[pair.Key] = pair.Value;
                    }
                }

                toCreate.KeyAttributes.Clear();
                affectedId = ExecuteCreate(toCreate, innerOf: upsertMain);
                response.Results["RecordCreated"] = true;
            }

            response.Results["Target"] = new EntityReference(target.LogicalName, affectedId);
            return response;
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

                return RunStages<object?>(
                    stage => FireStage(Make(stage), asyncQueue),
                    () =>
                    {
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

                        return null;
                    },
                    _ => FireStage(Make(PluginStages.PostOperation), asyncQueue));
            }, sharedVariables);
        }
    }
}
