using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.StepBuilder;

namespace Vela.Testing
{
    /// <summary>
    /// An in-memory simulation of the Dataverse plugin pipeline. Discover plugin registrations from an
    /// assembly (or register them explicitly), then drive writes through <see cref="Service"/>: each
    /// Create/Update/Delete/Associate (or Custom API <c>Execute</c>) fires the registered steps for the
    /// matching message/stage/entity, honouring filtering attributes, execution order, pre/post images and
    /// cascade (a plugin that writes triggers further plugins). Synchronous failures roll the operation
    /// back; asynchronous steps run after the synchronous "commit". This is scenario/behavioural testing —
    /// fully offline, not integration testing.
    /// </summary>
    public sealed class PluginPipeline
    {
        private readonly List<PipelineRegistration> _registrations = new List<PipelineRegistration>();
        private readonly Dictionary<string, Type> _customApis = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private readonly InMemoryOrganizationService _store = new InMemoryOrganizationService();
        private readonly List<PipelineExecutionEntry> _log = new List<PipelineExecutionEntry>();
        private readonly RecordingTracingService _trace = new RecordingTracingService();
        private readonly PipelineOrganizationService _service;
        private readonly Guid _correlationId = Guid.NewGuid();
        private int _maxDepth = 16;
        private int _depth;

        // Shared variables for the operation currently running. Seeded from the request 'tag' (or other
        // shared variables) at the top level, shared across every stage/plugin of the operation, and inherited
        // by cascaded (nested) writes — mirroring how Dataverse surfaces them (and ParentContext.SharedVariables).
        private ParameterCollection _sharedVariables = new ParameterCollection();

        // Bypass directives for the operation currently running, read from the request (BypassCustomPluginExecution
        // / BypassBusinessLogicExecution / BypassBusinessLogicExecutionStepIds). Empty = nothing bypassed.
        private BypassContext _bypass = BypassContext.None;

        private PluginPipeline()
        {
            _service = new PipelineOrganizationService(this);
        }

        /// <summary>Creates an empty pipeline; register plugins with <see cref="Register{TPlugin}"/>.</summary>
        public static PluginPipeline Empty()
        {
            return new PluginPipeline();
        }

        /// <summary>Discovers every Vela plugin and Custom API in the assembly and registers their steps.</summary>
        public static PluginPipeline FromAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            PluginPipeline pipeline = new PluginPipeline();
            FrameworkManifest manifest = FrameworkManifestScanner.Scan(assembly);

            foreach (StepDefinition step in manifest.PluginSteps)
            {
                Type? type = ResolveType(assembly, step.PluginTypeName);
                if (type != null)
                {
                    pipeline._registrations.Add(new PipelineRegistration(type, step));
                }
            }

            foreach (CustomApiDefinition api in manifest.CustomApis)
            {
                Type? type = ResolveType(assembly, api.PluginTypeName);
                if (type != null && !string.IsNullOrEmpty(api.UniqueName))
                {
                    pipeline._customApis[api.UniqueName] = type;
                }
            }

            return pipeline;
        }

        /// <summary>Discovers plugins from the assembly that contains <typeparamref name="T"/>.</summary>
        public static PluginPipeline FromAssemblyOf<T>()
        {
            return FromAssembly(typeof(T).Assembly);
        }

        /// <summary>Registers a single plugin (or Custom API) type explicitly.</summary>
        public PluginPipeline Register<TPlugin>()
            where TPlugin : IPlugin
        {
            return Register(typeof(TPlugin));
        }

        /// <summary>Registers a single plugin (or Custom API) type explicitly.</summary>
        public PluginPipeline Register(Type pluginType)
        {
            if (pluginType == null)
            {
                throw new ArgumentNullException(nameof(pluginType));
            }

            ConstructorInfo? constructor = pluginType.GetConstructor(new[] { typeof(string), typeof(string) });
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginType.FullName}' must have a (string, string) constructor to be registered.");
            }

            object instance = constructor.Invoke(new object?[] { null, null });

            MethodInfo? method = pluginType.GetMethod("GetStepDefinitions");
            if (method != null && method.Invoke(instance, null) is StepDefinition[] steps)
            {
                foreach (StepDefinition step in steps)
                {
                    _registrations.Add(new PipelineRegistration(pluginType, step));
                }
            }

            if (instance is CustomApiBase customApi)
            {
                CustomApiDefinition? definition = customApi.GetCustomApiDefinition();
                if (definition != null && !string.IsNullOrEmpty(definition.UniqueName))
                {
                    _customApis[definition.UniqueName] = pluginType;
                }
            }

            return this;
        }

        /// <summary>Pre-populates the store (no plugins fire).</summary>
        public PluginPipeline Seed(params Entity[] entities)
        {
            _store.Seed(entities);
            return this;
        }

        /// <summary>Declares an alternate key on the in-memory store (duplicate-key throws).</summary>
        public PluginPipeline WithAlternateKey(string entityLogicalName, params string[] keyAttributes)
        {
            _store.WithAlternateKey(entityLogicalName, keyAttributes);
            return this;
        }

        /// <summary>Declares an auto-number attribute on the in-memory store.</summary>
        public PluginPipeline WithAutoNumber(string entityLogicalName, string attribute, Func<int, object> generator)
        {
            _store.WithAutoNumber(entityLogicalName, attribute, generator);
            return this;
        }

        /// <summary>Declares a sequential auto-number attribute on the in-memory store.</summary>
        public PluginPipeline WithAutoNumber(string entityLogicalName, string attribute, string prefix, int length, string suffix = "")
        {
            _store.WithAutoNumber(entityLogicalName, attribute, prefix, length, suffix);
            return this;
        }

        /// <summary>Declares a many-to-many relationship so Associate/Disassociate persist its intersect rows.</summary>
        public PluginPipeline WithManyToMany(string relationshipSchemaName, string intersectEntity, string firstEntity, string firstAttribute, string secondEntity, string secondAttribute)
        {
            _store.WithManyToMany(relationshipSchemaName, intersectEntity, firstEntity, firstAttribute, secondEntity, secondAttribute);
            return this;
        }

        /// <summary>Strongly-typed many-to-many declaration: names come from the early-bound types and property accesses.</summary>
        public PluginPipeline WithManyToMany<TIntersect, TFirst, TSecond>(
            string relationshipSchemaName,
            Expression<Func<TIntersect, object?>> firstAttribute,
            Expression<Func<TIntersect, object?>> secondAttribute)
            where TIntersect : Entity
            where TFirst : Entity
            where TSecond : Entity
        {
            _store.WithManyToMany<TIntersect, TFirst, TSecond>(relationshipSchemaName, firstAttribute, secondAttribute);
            return this;
        }

        /// <summary>Registers table metadata readable via RetrieveEntity/RetrieveAllEntities/RetrieveAttribute.</summary>
        public PluginPipeline WithEntityMetadata(params Microsoft.Xrm.Sdk.Metadata.EntityMetadata[] metadata)
        {
            _store.WithEntityMetadata(metadata);
            return this;
        }

        /// <summary>
        /// Registers table metadata auto-derived from an early-bound type generated by <c>vela models</c> — no
        /// hand-written <see cref="FakeEntityMetadata"/>, no connection. Shorthand for
        /// <c>WithEntityMetadata(FakeEntityMetadata.From&lt;TEntity&gt;().Build())</c>.
        /// </summary>
        public PluginPipeline WithEntityMetadata<TEntity>() where TEntity : Microsoft.Xrm.Sdk.Entity
        {
            _store.WithEntityMetadata<TEntity>();
            return this;
        }

        /// <summary>Sets the calling user stamped into <c>createdby</c>/<c>modifiedby</c>/<c>ownerid</c> and matched by <c>EqualUserId</c>.</summary>
        public PluginPipeline WithCurrentUser(Guid userId)
        {
            _store.WithCurrentUser(userId);
            return this;
        }

        /// <summary>Sets the business unit stamped into <c>owningbusinessunit</c> on user/team-owned writes.</summary>
        public PluginPipeline WithCurrentBusinessUnit(Guid businessUnitId)
        {
            _store.WithCurrentBusinessUnit(businessUnitId);
            return this;
        }

        /// <summary>Sets the organization id stamped into <c>organizationid</c> on organization-owned writes.</summary>
        public PluginPipeline WithOrganizationId(Guid organizationId)
        {
            _store.WithOrganizationId(organizationId);
            return this;
        }

        /// <summary>Overrides the clock used to stamp <c>createdon</c>/<c>modifiedon</c> (default <see cref="DateTime.UtcNow"/>).</summary>
        public PluginPipeline WithClock(Func<DateTime> clock)
        {
            _store.WithClock(clock);
            return this;
        }

        /// <summary>Overrides how ids are generated for records created without one, so they are reproducible.</summary>
        public PluginPipeline WithIdSequence(Func<Guid> sequence)
        {
            _store.WithIdSequence(sequence);
            return this;
        }

        /// <summary>Installs a deterministic sequential id generator (see <see cref="SequentialIds"/>).</summary>
        public PluginPipeline WithIdSequence()
        {
            _store.WithIdSequence();
            return this;
        }

        /// <summary>Declares a table as organization-owned (audit columns only, no owner columns).</summary>
        public PluginPipeline AsOrganizationOwned(string entityLogicalName)
        {
            _store.AsOrganizationOwned(entityLogicalName);
            return this;
        }

        /// <summary>Declares a table as user/team-owned (audit + owner columns); overrides inference for late-bound rows.</summary>
        public PluginPipeline AsUserOwned(string entityLogicalName)
        {
            _store.AsUserOwned(entityLogicalName);
            return this;
        }

        /// <summary>Declares a table as virtual (external data): no system columns are auto-populated.</summary>
        public PluginPipeline AsVirtual(string entityLogicalName)
        {
            _store.AsVirtual(entityLogicalName);
            return this;
        }

        /// <summary>Declares a virtual table, optionally read-only so writes are rejected.</summary>
        public PluginPipeline AsVirtual(string entityLogicalName, bool readOnly)
        {
            _store.AsVirtual(entityLogicalName, readOnly);
            return this;
        }

        /// <summary>Declares a table as elastic (audit columns only, no owner columns).</summary>
        public PluginPipeline AsElastic(string entityLogicalName)
        {
            _store.AsElastic(entityLogicalName);
            return this;
        }

        /// <summary>Sets the cascade-depth safety cap (default 16) that aborts runaway recursion.</summary>
        public PluginPipeline WithMaxDepth(int maxDepth)
        {
            _maxDepth = maxDepth < 1 ? 1 : maxDepth;
            return this;
        }

        /// <summary>The pipeline-aware service: drive the scenario through it, and plugins receive it for cascade.</summary>
        public IOrganizationService Service => _service;

        /// <summary>Ordered log of every step that fired (for asserting what ran and in what order).</summary>
        public IReadOnlyList<PipelineExecutionEntry> ExecutionLog => _log;

        /// <summary>Captured traces aggregated across the whole cascade.</summary>
        public RecordingTracingService Trace => _trace;

        /// <summary>Snapshot (clones) of all stored records of a logical name, for assertions.</summary>
        public IReadOnlyList<Entity> GetAll(string logicalName) => _store.GetAll(logicalName);

        internal InMemoryOrganizationService Store => _store;

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

        // A single Update is the one-row case of an Update batch (see ExecuteCreate).
        // Online, an Update that carries owner (ownerid) or state/status (statecode/statuscode) fields ALONGSIDE
        // standard fields fires the Update step twice: once for the standard fields, then once for the special
        // (owner/state) fields. Mirror that split so plugins see the same two executions.
        internal void ExecuteUpdate(Entity entity, ParameterCollection? sharedVariables = null)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            Entity? standard = ProjectAttributes(entity, special: false);
            Entity? special = ProjectAttributes(entity, special: true);

            if (standard != null && special != null)
            {
                ExecuteUpdateMultiple(new[] { standard }, sharedVariables);
                ExecuteUpdateMultiple(new[] { special }, sharedVariables);
            }
            else
            {
                ExecuteUpdateMultiple(new[] { entity }, sharedVariables);
            }
        }

        // The owner / state-status fields the platform processes as a separate Update execution.
        private static readonly HashSet<string> SpecialUpdateAttributes =
            new HashSet<string>(new[] { "ownerid", "statecode", "statuscode" }, StringComparer.OrdinalIgnoreCase);

        // Returns a copy of the entity holding only the special (or only the non-special) attributes, or null
        // when it has none of that kind. The id/logical name are preserved.
        private static Entity? ProjectAttributes(Entity entity, bool special)
        {
            Entity projected = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                if (SpecialUpdateAttributes.Contains(attribute.Key) == special)
                {
                    projected[attribute.Key] = attribute.Value;
                }
            }

            return projected.Attributes.Count > 0 ? projected : null;
        }

        // Maps the deprecated SetState messages to a statecode/statuscode Update so the Update pipeline runs
        // (mirroring how AssignRequest is modelled as an ownerid update). The status defaults from the state when
        // omitted: state 0 (active) -> status 1, otherwise status 2 — the standard default for most tables.
        internal void ExecuteSetState(EntityReference target, OptionSetValue? state, OptionSetValue? status, ParameterCollection? sharedVariables = null)
        {
            int stateValue = state?.Value ?? 0;
            int statusValue = status != null && status.Value >= 0 ? status.Value : (stateValue == 0 ? 1 : 2);

            ExecuteUpdate(
                new Entity(target.LogicalName, target.Id)
                {
                    ["statecode"] = new OptionSetValue(stateValue),
                    ["statuscode"] = new OptionSetValue(statusValue)
                },
                sharedVariables);
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

            return RunOperation(asyncQueue =>
            {
                string entityName = entities.Length > 0 ? entities[0].LogicalName : string.Empty;
                List<BatchItem> items = entities.Select(e => new BatchItem { Target = e }).ToList();

                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreValidation, entityName, items, asyncQueue);
                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreOperation, entityName, items, asyncQueue);

                Guid[] ids = new Guid[entities.Length];
                for (int i = 0; i < entities.Length; i++)
                {
                    ids[i] = _store.Create(entities[i]);
                    entities[i].Id = ids[i]; // the post-operation Target carries the created id
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

            RunOperation<object?>(asyncQueue =>
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

                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreValidation, entityName, items, asyncQueue);
                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreOperation, entityName, items, asyncQueue);

                foreach (Entity entity in entities)
                {
                    _store.Update(entity);
                }

                foreach (BatchItem item in items)
                {
                    _store.TryGet(item.Target.LogicalName, item.Target.Id, out Entity? post);
                    item.PostState = post;
                }

                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PostOperation, entityName, items, asyncQueue);
                return null;
            }, sharedVariables);
        }

        internal void ExecuteDelete(string entityName, Guid id, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(asyncQueue =>
            {
                _store.TryGet(entityName, id, out Entity? pre);
                EntityReference target = new EntityReference(entityName, id);

                StageInvocation Make(PluginStages stage)
                {
                    StageInvocation invocation = NewInvocation(PluginMessages.Delete, stage, entityName, id, target);
                    invocation.PreState = pre;
                    return invocation;
                }

                FireStage(Make(PluginStages.PreValidation), asyncQueue);
                FireStage(Make(PluginStages.PreOperation), asyncQueue);
                _store.Delete(entityName, id);
                FireStage(Make(PluginStages.PostOperation), asyncQueue);
                return null;
            }, sharedVariables);
        }

        internal void ExecuteAssociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, ParameterCollection? sharedVariables = null)
        {
            RunAssociation(PluginMessages.Associate, entityName, entityId, relationship, relatedEntities, sharedVariables);
        }

        internal void ExecuteDisassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, ParameterCollection? sharedVariables = null)
        {
            RunAssociation(PluginMessages.Disassociate, entityName, entityId, relationship, relatedEntities, sharedVariables);
        }

        internal OrganizationResponse ExecuteRequest(OrganizationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Bypass parameters apply to the whole top-level operation (and its cascade); a nested Execute
            // inherits the active bypass, mirroring how shared variables flow.
            BypassContext previousBypass = _bypass;
            if (_depth == 0)
            {
                _bypass = ExtractBypass(request);
            }

            try
            {
                return ExecuteRequestCore(request);
            }
            finally
            {
                _bypass = previousBypass;
            }
        }

        private OrganizationResponse ExecuteRequestCore(OrganizationRequest request)
        {
            ParameterCollection shared = ExtractSharedVariables(request);

            if (_customApis.TryGetValue(request.RequestName, out Type? pluginType))
            {
                return RunOperation(asyncQueue =>
                {
                    FakePluginExecutionContext context = new FakePluginExecutionContext
                    {
                        MessageName = request.RequestName,
                        Stage = (int)PluginStages.MainOperation,
                        Mode = (int)ExecutionMode.Synchronous,
                        Depth = _depth,
                        PrimaryEntityName = string.Empty,
                        CorrelationId = _correlationId,
                        SharedVariables = _sharedVariables
                    };

                    foreach (KeyValuePair<string, object> parameter in request.Parameters)
                    {
                        context.InputParameters[parameter.Key] = parameter.Value;
                    }

                    _log.Add(new PipelineExecutionEntry(pluginType.FullName ?? pluginType.Name, request.RequestName, PluginStages.MainOperation, ExecutionMode.Synchronous, string.Empty, _depth));
                    IPlugin plugin = Instantiate(pluginType);
                    plugin.Execute(BuildProvider(context));

                    OrganizationResponse response = new OrganizationResponse();
                    foreach (KeyValuePair<string, object> output in context.OutputParameters)
                    {
                        response.Results[output.Key] = output.Value;
                    }

                    return response;
                }, shared);
            }

            // Pipeline-aware messages run their registered steps and carry any shared variables the request set
            // (e.g. the client 'tag'). Other messages (metadata/ExecuteMultiple...) hit the store.
            switch (request)
            {
                case CreateRequest create:
                {
                    CreateResponse response = new CreateResponse();
                    response.Results["id"] = ExecuteCreate(create.Target, shared);
                    return response;
                }

                case UpdateRequest update:
                    ExecuteUpdate(update.Target, shared);
                    return new UpdateResponse();

                case CreateMultipleRequest createMultiple:
                {
                    Guid[] ids = ExecuteCreateMultiple(createMultiple.Targets.Entities.ToArray(), shared);
                    CreateMultipleResponse response = new CreateMultipleResponse();
                    response.Results["Ids"] = ids;
                    return response;
                }

                case UpdateMultipleRequest updateMultiple:
                    ExecuteUpdateMultiple(updateMultiple.Targets.Entities.ToArray(), shared);
                    return new UpdateMultipleResponse();

                case DeleteRequest delete:
                    ExecuteDelete(delete.Target.LogicalName, delete.Target.Id, shared);
                    return new DeleteResponse();

                case AssociateRequest associate:
                    ExecuteAssociate(associate.Target.LogicalName, associate.Target.Id, associate.Relationship, associate.RelatedEntities, shared);
                    return new AssociateResponse();

                case DisassociateRequest disassociate:
                    ExecuteDisassociate(disassociate.Target.LogicalName, disassociate.Target.Id, disassociate.Relationship, disassociate.RelatedEntities, shared);
                    return new DisassociateResponse();

                case SetStateRequest setState:
                    ExecuteSetState(setState.EntityMoniker, setState.State, setState.Status, shared);
                    return new SetStateResponse();

                default:
                    return _store.Execute(request);
            }
        }

        // Dataverse promotes the 'tag' request parameter to a shared variable readable by plugins via
        // IExecutionContext.SharedVariables. (The shortcut Create/Update/... methods can't carry it — only Execute.)
        private static ParameterCollection ExtractSharedVariables(OrganizationRequest request)
        {
            ParameterCollection shared = new ParameterCollection();
            if (request.Parameters.TryGetValue("tag", out object? tag) && tag != null)
            {
                shared["tag"] = tag;
            }

            return shared;
        }

        // Reads the platform's "skip business logic" request parameters. BypassCustomPluginExecution skips every
        // custom step; BypassBusinessLogicExecution skips by mode (CustomSync/CustomAsync); and
        // BypassBusinessLogicExecutionStepIds skips specific steps — matched here against the step key (the
        // harness step identity), accepting either a comma-separated string or a collection of keys/ids.
        private static BypassContext ExtractBypass(OrganizationRequest request)
        {
            BypassContext bypass = new BypassContext();

            if (request.Parameters.TryGetValue("BypassCustomPluginExecution", out object? all) && all is bool allBool)
            {
                bypass.AllCustom = allBool;
            }

            if (request.Parameters.TryGetValue("BypassBusinessLogicExecution", out object? mode) && mode != null)
            {
                string text = mode.ToString() ?? string.Empty;
                bypass.BypassSync = text.IndexOf("CustomSync", StringComparison.OrdinalIgnoreCase) >= 0;
                bypass.BypassAsync = text.IndexOf("CustomAsync", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (request.Parameters.TryGetValue("BypassBusinessLogicExecutionStepIds", out object? ids) && ids != null)
            {
                foreach (string key in EnumerateStepKeys(ids))
                {
                    bypass.StepKeys.Add(key);
                }
            }

            return bypass;
        }

        private static IEnumerable<string> EnumerateStepKeys(object value)
        {
            if (value is string text)
            {
                foreach (string part in text.Split(','))
                {
                    string trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }

                yield break;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (object? item in sequence)
                {
                    string? key = item?.ToString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        yield return key!;
                    }
                }
            }
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

                foreach (PluginStages stage in new[] { PluginStages.PreValidation, PluginStages.PreOperation })
                {
                    StageInvocation preInvocation = NewInvocation(message, stage, entityName, entityId, target);
                    preInvocation.ExtraInputs = extra;
                    FireStage(preInvocation, asyncQueue);
                }

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

                StageInvocation postInvocation = NewInvocation(message, PluginStages.PostOperation, entityName, entityId, target);
                postInvocation.ExtraInputs = extra;
                FireStage(postInvocation, asyncQueue);

                return null;
            }, sharedVariables);
        }

        private T RunOperation<T>(Func<List<Action>, T> body, ParameterCollection? sharedVariables = null)
        {
            _depth++;
            ParameterCollection previousShared = _sharedVariables;
            // Top-level operation starts from the supplied shared variables (e.g. the request 'tag'); a cascaded
            // write inherits the parent operation's collection so upstream shared variables remain visible.
            _sharedVariables = _depth == 1 ? (sharedVariables ?? new ParameterCollection()) : previousShared;
            try
            {
                if (_depth > _maxDepth)
                {
                    throw new InvalidPluginExecutionException(
                        $"Vela pipeline stopped at depth {_depth} (cap {_maxDepth}). Likely infinite recursion between plugins.");
                }

                Dictionary<string, Dictionary<Guid, Entity>> snapshot = _store.Snapshot();
                List<Action> asyncQueue = new List<Action>();
                T result;
                try
                {
                    result = body(asyncQueue);
                }
                catch
                {
                    _store.RestoreTo(snapshot);
                    throw;
                }

                // Asynchronous steps run after the synchronous transaction "commits"; their failures do not roll back.
                foreach (Action pending in asyncQueue)
                {
                    pending();
                }

                return result;
            }
            finally
            {
                _sharedVariables = previousShared;
                _depth--;
            }
        }

        private void FireStage(StageInvocation invocation, List<Action> asyncQueue)
        {
            List<PipelineRegistration> steps = _registrations
                .Where(registration => registration.Step.Stage == invocation.Stage
                    && string.Equals(registration.Step.Message, invocation.Message, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(registration.Step.EntityLogicalName, invocation.EntityLogicalName, StringComparison.OrdinalIgnoreCase))
                .Where(registration => !string.Equals(invocation.Message, PluginMessages.Update, StringComparison.OrdinalIgnoreCase)
                    || FiltersMatch(registration.Step, invocation.ChangedAttributes))
                .Where(registration => !_bypass.ShouldBypass(registration.Step))
                .OrderBy(registration => registration.Step.ExecutionOrder)
                .ToList();

            foreach (PipelineRegistration registration in steps)
            {
                PipelineRegistration captured = registration;
                if (captured.Step.Mode == ExecutionMode.Asynchronous)
                {
                    asyncQueue.Add(() => Dispatch(captured, invocation));
                }
                else
                {
                    Dispatch(captured, invocation);
                }
            }
        }

        // Fires one stage of a Create/Update batch. The two registration messages are merged: a bulk step
        // (CreateMultiple/UpdateMultiple) fires once with the whole Targets collection; a single step
        // (Create/Update) fires once per row. Both are ordered by execution order; Update steps are filtered.
        private void FireBatchStage(string singleMessage, string bulkMessage, PluginStages stage, string entityName, IReadOnlyList<BatchItem> items, List<Action> asyncQueue)
        {
            bool isUpdate = string.Equals(singleMessage, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);

            List<PipelineRegistration> matching = _registrations
                .Where(registration => registration.Step.Stage == stage
                    && string.Equals(registration.Step.EntityLogicalName, entityName, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(registration.Step.Message, singleMessage, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(registration.Step.Message, bulkMessage, StringComparison.OrdinalIgnoreCase)))
                .Where(registration => !_bypass.ShouldBypass(registration.Step))
                .OrderBy(registration => registration.Step.ExecutionOrder)
                .ToList();

            if (matching.Count == 0)
            {
                return;
            }

            EntityCollection? targets = null;

            foreach (PipelineRegistration registration in matching)
            {
                PipelineRegistration captured = registration;
                bool isBulk = string.Equals(registration.Step.Message, bulkMessage, StringComparison.OrdinalIgnoreCase);
                if (isBulk)
                {
                    // A bulk step fires once for the batch. For UpdateMultiple, fire if any row matches the filter.
                    if (isUpdate && !items.Any(item => FiltersMatch(captured.Step, item.ChangedAttributes)))
                    {
                        continue;
                    }

                    EntityCollection capturedTargets = targets ??= BuildTargets(entityName, items);
                    Emit(captured, asyncQueue, () => DispatchBulk(captured, bulkMessage, stage, entityName, capturedTargets, items));
                }
                else
                {
                    foreach (BatchItem item in items)
                    {
                        if (isUpdate && !FiltersMatch(captured.Step, item.ChangedAttributes))
                        {
                            continue;
                        }

                        BatchItem capturedItem = item;
                        StageInvocation invocation = NewInvocation(singleMessage, stage, entityName, capturedItem.Target.Id, capturedItem.Target);
                        invocation.PreState = capturedItem.PreState;
                        invocation.PostState = capturedItem.PostState;
                        invocation.ChangedAttributes = capturedItem.ChangedAttributes;
                        Emit(captured, asyncQueue, () => Dispatch(captured, invocation));
                    }
                }
            }
        }

        // Runs the dispatch now for a synchronous step, or defers it to after the synchronous commit for an
        // asynchronous one — the same sync/async split FireStage applies to single-operation steps.
        private static void Emit(PipelineRegistration registration, List<Action> asyncQueue, Action dispatch)
        {
            if (registration.Step.Mode == ExecutionMode.Asynchronous)
            {
                asyncQueue.Add(dispatch);
            }
            else
            {
                dispatch();
            }
        }

        private static EntityCollection BuildTargets(string entityName, IReadOnlyList<BatchItem> items)
        {
            EntityCollection collection = new EntityCollection { EntityName = entityName };
            foreach (BatchItem item in items)
            {
                collection.Entities.Add(item.Target);
            }

            return collection;
        }

        private void DispatchBulk(PipelineRegistration registration, string message, PluginStages stage, string entityName, EntityCollection targets, IReadOnlyList<BatchItem> items)
        {
            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                MessageName = message,
                Stage = (int)stage,
                Mode = (int)registration.Step.Mode,
                Depth = _depth,
                PrimaryEntityName = entityName,
                PrimaryEntityId = Guid.Empty,
                CorrelationId = _correlationId,
                SharedVariables = _sharedVariables
            };

            // Bulk steps read the batch via ctx.BulkTargets (InputParameters["Targets"]) and per-row pre/post
            // images via the image collections (aligned by target index).
            context.InputParameters["Targets"] = targets;
            context.PreEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PreImages, item.PreState)).ToArray();
            context.PostEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PostImages, item.PostState)).ToArray();

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, message, stage, registration.Step.Mode, entityName, _depth));
            IPlugin plugin = registration.CreateInstance();
            plugin.Execute(BuildProvider(context));
        }

        private static EntityImageCollection BuildImageCollection(ImageDefinition[] definitions, Entity? state)
        {
            EntityImageCollection collection = new EntityImageCollection();
            BuildImages(collection, definitions, state);
            return collection;
        }

        private void Dispatch(PipelineRegistration registration, StageInvocation invocation)
        {
            FakePluginExecutionContext context = new FakePluginExecutionContext
            {
                MessageName = invocation.Message,
                Stage = (int)invocation.Stage,
                Mode = (int)registration.Step.Mode,
                Depth = _depth,
                PrimaryEntityName = invocation.EntityLogicalName,
                PrimaryEntityId = invocation.PrimaryId,
                CorrelationId = _correlationId,
                SharedVariables = _sharedVariables
            };

            if (invocation.Target is Entity entityTarget)
            {
                context.InputParameters["Target"] = entityTarget;
            }
            else if (invocation.Target is EntityReference referenceTarget)
            {
                context.InputParameters["Target"] = referenceTarget;
            }

            if (invocation.ExtraInputs != null)
            {
                foreach (KeyValuePair<string, object> input in invocation.ExtraInputs)
                {
                    context.InputParameters[input.Key] = input.Value;
                }
            }

            BuildImages(context.PreEntityImages, registration.Step.PreImages, invocation.PreState);
            BuildImages(context.PostEntityImages, registration.Step.PostImages, invocation.PostState);

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, invocation.Message, invocation.Stage, registration.Step.Mode, invocation.EntityLogicalName, _depth));
            IPlugin plugin = registration.CreateInstance();
            plugin.Execute(BuildProvider(context));
        }

        private IServiceProvider BuildProvider(FakePluginExecutionContext context)
        {
            HookOrganizationServiceFactory factory = new HookOrganizationServiceFactory
            {
                UserService = _service,
                SystemService = _service
            };

            Dictionary<Type, object> services = new Dictionary<Type, object>
            {
                [typeof(IPluginExecutionContext)] = context,
                [typeof(IOrganizationServiceFactory)] = factory,
                [typeof(ITracingService)] = _trace
            };

            return new DictionaryServiceProvider(services);
        }

        private static IPlugin Instantiate(Type pluginType)
        {
            ConstructorInfo constructor = pluginType.GetConstructor(new[] { typeof(string), typeof(string) })
                ?? throw new InvalidOperationException($"Plugin '{pluginType.FullName}' must have a (string, string) constructor.");
            return (IPlugin)constructor.Invoke(new object?[] { null, null });
        }

        private static void BuildImages(EntityImageCollection target, ImageDefinition[] definitions, Entity? state)
        {
            if (state == null || definitions == null)
            {
                return;
            }

            foreach (ImageDefinition definition in definitions)
            {
                target[definition.Alias] = ProjectImage(state, definition.Attributes);
            }
        }

        private static Entity ProjectImage(Entity state, string[] attributes)
        {
            Entity image = new Entity(state.LogicalName) { Id = state.Id };
            if (attributes == null || attributes.Length == 0)
            {
                foreach (KeyValuePair<string, object> attribute in state.Attributes)
                {
                    image[attribute.Key] = attribute.Value;
                }
            }
            else
            {
                foreach (string name in attributes)
                {
                    if (state.Contains(name))
                    {
                        image[name] = state[name];
                    }
                }
            }

            return image;
        }

        private static bool FiltersMatch(StepDefinition step, ISet<string>? changedAttributes)
        {
            if (step.FilteringAttributes == null || step.FilteringAttributes.Length == 0 || changedAttributes == null)
            {
                return true;
            }

            return step.FilteringAttributes.Any(changedAttributes.Contains);
        }

        private static StageInvocation NewInvocation(string message, PluginStages stage, string entityLogicalName, Guid primaryId, object? target)
        {
            return new StageInvocation
            {
                Message = message,
                Stage = stage,
                EntityLogicalName = entityLogicalName,
                PrimaryId = primaryId,
                Target = target
            };
        }

        private static StageInvocation UpdateInvocation(PluginStages stage, Entity target, Entity? pre, Entity? post, ISet<string> changed)
        {
            StageInvocation invocation = NewInvocation(PluginMessages.Update, stage, target.LogicalName, target.Id, target);
            invocation.PreState = pre;
            invocation.PostState = post;
            invocation.ChangedAttributes = changed;
            return invocation;
        }

        private static Type? ResolveType(Assembly assembly, string fullName)
        {
            return string.IsNullOrEmpty(fullName) ? null : assembly.GetType(fullName);
        }

        // The bypass directives in force for the current operation. ShouldBypass decides whether a registered
        // step is suppressed (by "all custom", by execution mode, or by step key).
        private sealed class BypassContext
        {
            public static readonly BypassContext None = new BypassContext();

            public bool AllCustom;
            public bool BypassSync;
            public bool BypassAsync;
            public readonly HashSet<string> StepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool ShouldBypass(StepDefinition step)
            {
                return AllCustom
                    || (BypassSync && step.Mode == ExecutionMode.Synchronous)
                    || (BypassAsync && step.Mode == ExecutionMode.Asynchronous)
                    || (step.StepKey.Length > 0 && StepKeys.Contains(step.StepKey));
            }
        }

        // One row of a Create/Update batch: the mutable Target (shared with the committed entity and the bulk
        // Targets collection) plus its pre/post state and changed attributes for image projection and filtering.
        private sealed class BatchItem
        {
            public Entity Target = null!;
            public Entity? PreState;
            public Entity? PostState;
            public ISet<string>? ChangedAttributes;
        }

        private sealed class StageInvocation
        {
            public string Message = string.Empty;
            public PluginStages Stage;
            public string EntityLogicalName = string.Empty;
            public Guid PrimaryId;
            public object? Target;
            public Entity? PreState;
            public Entity? PostState;
            public IDictionary<string, object>? ExtraInputs;
            public ISet<string>? ChangedAttributes;
        }
    }
}
