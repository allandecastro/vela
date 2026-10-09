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
    /// <summary>
    /// An in-memory simulation of the Dataverse plugin pipeline. Discover plugin registrations from an
    /// assembly (or register them explicitly), then drive writes through <see cref="Service"/>: each
    /// Create/Update/Delete/Associate (or Custom API <c>Execute</c>) fires the registered steps for the
    /// matching message/stage/entity, honouring filtering attributes, execution order, pre/post images and
    /// cascade (a plugin that writes triggers further plugins). Synchronous failures roll the operation
    /// back; asynchronous steps run once the outermost operation commits, and never when it rolls back. This is
    /// scenario/behavioural testing — fully offline, not integration testing.
    /// </summary>
    public sealed class PluginPipeline
    {
        private readonly List<PipelineRegistration> _registrations = new List<PipelineRegistration>();
        private readonly Dictionary<string, Type> _customApis = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private readonly InMemoryOrganizationService _store = new InMemoryOrganizationService();
        private readonly List<PipelineExecutionEntry> _log = new List<PipelineExecutionEntry>();
        private readonly List<Exception> _asyncStepFailures = new List<Exception>();
        private readonly RecordingTracingService _trace = new RecordingTracingService();
        private readonly RecordingLogger _logger = new RecordingLogger();
        private readonly PipelineOrganizationService _service;
        private int _maxDepth = 16;
        private int _depth;

        // One correlation id per top-level operation, shared by everything it cascades into, including the async
        // steps it queues (each restores the id captured when it was queued). Verified live: an ExecuteTransaction
        // batch shares one id, each child of an ExecuteMultiple gets its own.
        private Guid _correlationId = Guid.NewGuid();

        // One request id per call into the pipeline from outside (an ExecuteMultiple batch is one request), carried by
        // every step it triggers, async ones included. Verified live: a sync step's OperationId is the request id, an
        // async step's is its own system job id.
        private Guid _requestId = Guid.NewGuid();
        private bool _inRequest;
        private Guid _asyncOperationId;

        // Asynchronous steps waiting for the outermost scope to commit. Live, an async step becomes a system job only
        // when the triggering transaction commits, and a rolled-back operation creates no job at all (verified live
        // 2026-10-07), so a scope that rolls back drops the steps it queued (see RunAtomically).
        private readonly List<PendingAsyncStep> _pendingAsyncSteps = new List<PendingAsyncStep>();

        // All-or-nothing scopes currently open: operations, ExecuteTransaction, the two passes of a split Update, and a
        // running async step. Async steps run only once the count drops back to zero.
        private int _openScopes;

        // Database transactions currently open. Every operation opens one except the non-transactional reads, and so
        // does ExecuteTransaction; an async step runs outside any transaction.
        private int _openTransactions;

        // Whether the running operation started inside an already open transaction. Its PreValidation steps run before
        // its own transaction begins, so this alone decides their IsInTransaction (verified live: false at depth 1,
        // true for a nested PreValidation).
        private bool _operationEnclosedInTransaction;

        // The execution contexts currently on the (synchronous) call stack. The context of the plugin that is
        // executing when a cascaded write re-enters the pipeline becomes the nested execution's ParentContext,
        // mirroring how Dataverse exposes IPluginExecutionContext.ParentContext (and its SharedVariables) to a
        // plugin fired by another plugin's write.
        private readonly Stack<FakePluginExecutionContext> _contextStack = new Stack<FakePluginExecutionContext>();

        // Shared variables for the operation currently running. Seeded from the request 'tag' (or other
        // shared variables) at the top level, shared across every stage/plugin of the operation, and inherited
        // by cascaded (nested) writes — mirroring how Dataverse surfaces them (and ParentContext.SharedVariables).
        private ParameterCollection _sharedVariables = new ParameterCollection();

        // Bypass directives for the operation currently running, read from the request (BypassCustomPluginExecution
        // / BypassBusinessLogicExecution / BypassBusinessLogicExecutionStepIds). Empty = nothing bypassed.
        private BypassContext _bypass = BypassContext.None;

        // Secure configuration values by name (WithSecureConfig). A step only carries its SecureConfigName; the
        // value is supplied at deployment time (vela apply --secure-config NAME=VALUE), so the test supplies it here.
        private readonly Dictionary<string, string> _secureConfigs = new Dictionary<string, string>(StringComparer.Ordinal);

        // The identity of the service issuing the current call (null = the test-facing service: the configured
        // current user) and the user who initiated the outermost request (null until a plugin-issued call runs).
        private Guid? _callerUserId;
        private Guid? _initiatingUserId;

        /// <summary>
        /// The user id a plugin execution context carries when it was triggered by a write issued through the SYSTEM
        /// service (<c>ctx.SystemService</c>), standing in for the org's SYSTEM user. A service created for this id
        /// (e.g. <c>ctx.Service</c> in that nested plugin) is the unrestricted SYSTEM service, as on a real org.
        /// Internal: tests reason about the behaviour (SYSTEM is unrestricted), not about this id.
        /// </summary>
        internal static readonly Guid SystemUserId = new Guid("5e5e5e00-0000-0000-0000-000000000000");

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

        /// <summary>
        /// Supplies the value of a named secure configuration. A step declared with <c>.WithSecureConfigFrom(name)</c>
        /// receives this value as the <c>secureConfig</c> constructor argument, exactly as <c>vela apply
        /// --secure-config NAME=VALUE</c> provides it in production. A step whose name has no value receives null.
        /// </summary>
        public PluginPipeline WithSecureConfig(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A secure configuration name is required.", nameof(name));
            }

            _secureConfigs[name] = value;
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

        /// <summary>Declares a 1:N relationship and its cascade behaviour (see the store's <c>WithRelationship</c>).</summary>
        public PluginPipeline WithRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, Microsoft.Xrm.Sdk.Metadata.CascadeConfiguration cascade)
        {
            _store.WithRelationship(referencedEntity, referencingEntity, referencingAttribute, cascade);
            return this;
        }

        /// <summary>Declares a parental 1:N relationship (every action, incl. Delete, cascades to children).</summary>
        public PluginPipeline WithParentalRelationship(string referencedEntity, string referencingEntity, string referencingAttribute)
        {
            _store.WithParentalRelationship(referencedEntity, referencingEntity, referencingAttribute);
            return this;
        }

        /// <summary>Declares a referential 1:N relationship (Delete removes the child link, or Restrict blocks it).</summary>
        public PluginPipeline WithReferentialRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, bool restrictDelete = false)
        {
            _store.WithReferentialRelationship(referencedEntity, referencingEntity, referencingAttribute, restrictDelete);
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

        /// <summary>
        /// Enables opt-in security enforcement for the scenario (see <c>InMemoryOrganizationService.WithSecurity</c>).
        /// Every write the pipeline drives and every read plugins issue through <see cref="Service"/> is then checked
        /// against the current user's roles; a denied write throws PrincipalPrivilegeDenied and rolls the operation
        /// back. Off by default. Set the current user with <see cref="WithCurrentUser"/>.
        /// </summary>
        public PluginPipeline WithSecurity(Action<SecurityConfiguration> configure)
        {
            _store.WithSecurity(configure);
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

        /// <summary>
        /// Exceptions thrown by asynchronous steps. Real Dataverse runs async steps as decoupled system jobs whose
        /// failures never surface to the triggering caller, so the pipeline records them here instead of throwing
        /// out of the originating operation. Assert on this to verify an async step failed.
        /// </summary>
        public IReadOnlyList<Exception> AsyncStepFailures => _asyncStepFailures;

        /// <summary>Captured traces aggregated across the whole cascade.</summary>
        public RecordingTracingService Trace => _trace;

        /// <summary>
        /// Captured Application Insights entries and metrics (<c>ctx.LogInformation</c>, <c>ctx.LogMetric</c>, ...)
        /// aggregated across the whole cascade.
        /// </summary>
        public RecordingLogger LogCapture => _logger;

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

            entity = ResolveKeyedTarget(entity);
            Entity? standard = ProjectAttributes(entity, special: false);
            Entity? special = ProjectAttributes(entity, special: true);

            if (standard != null && special != null)
            {
                // The two step-firings are one logical Update: run them in a single snapshot scope so a failure in the
                // second (owner/state) pass rolls back the first (standard) pass too, instead of leaving the record
                // half-updated. Each ExecuteUpdateMultiple still snapshots internally; this outer scope makes the pair
                // atomic across passes, and holds back the first pass's async steps until the second pass commits.
                RunAtomically(() =>
                {
                    ExecuteUpdateMultiple(new[] { standard }, sharedVariables);
                    ExecuteUpdateMultiple(new[] { special }, sharedVariables);
                    return true;
                }, transactional: false);
            }
            else
            {
                ExecuteUpdateMultiple(new[] { entity }, sharedVariables);
            }
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

        // A copy of a write target for the steps to work on: its attributes, alternate key and deep-insert children
        // (each child is copied in turn when it is created).
        private static Entity CloneTarget(Entity entity)
        {
            Entity clone = EntityValues.Clone(entity);
            foreach (KeyValuePair<string, object> key in entity.KeyAttributes)
            {
                clone.KeyAttributes[key.Key] = key.Value;
            }

            foreach (KeyValuePair<Relationship, EntityCollection> related in entity.RelatedEntities)
            {
                clone.RelatedEntities[related.Key] = related.Value;
            }

            return clone;
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

            // The steps work on copies: live, the request is serialized, so neither a plugin's change to Target nor the
            // created id ever reaches the caller's entity (and so cannot survive a rollback on it).
            entities = entities.Select(CloneTarget).ToArray();
            return RunOperation(() =>
            {
                string entityName = entities.Length > 0 ? entities[0].LogicalName : string.Empty;
                // FilterOn applies to Create as it does to Update: a Create step fires only when the request sets one
                // of its filtering attributes, so the "changed" set of a create is the attributes it carries.
                List<BatchItem> items = entities.Select(e => new BatchItem
                {
                    Target = e,
                    ChangedAttributes = new HashSet<string>(e.Attributes.Keys, StringComparer.OrdinalIgnoreCase)
                }).ToList();

                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreValidation, entityName, items);
                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PreOperation, entityName, items);

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

                FireBatchStage(PluginMessages.Create, PluginMessages.CreateMultiple, PluginStages.PostOperation, entityName, items);
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

            // ResolveKeyedTarget may return the caller's own entity; the steps work on a copy (see ExecuteCreateMultiple).
            entities = entities.Select(entity => CloneTarget(ResolveKeyedTarget(entity))).ToArray();
            RunOperation<object?>(() =>
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

                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreValidation, entityName, items);
                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PreOperation, entityName, items);

                foreach (Entity entity in entities)
                {
                    _store.Update(entity);
                }

                foreach (BatchItem item in items)
                {
                    _store.TryGet(item.Target.LogicalName, item.Target.Id, out Entity? post);
                    item.PostState = post;
                }

                FireBatchStage(PluginMessages.Update, PluginMessages.UpdateMultiple, PluginStages.PostOperation, entityName, items);
                return null;
            }, sharedVariables);
        }

        internal void ExecuteDelete(string entityName, Guid id, ParameterCollection? sharedVariables = null)
        {
            RunOperation<object?>(() =>
            {
                // PreValidation runs before the main operation, so before any cascade: the deleted record's
                // PreValidation steps still see its children (verified live: 2 children at PreValidation, 0 at
                // PreOperation). A missing record skips it and lets the store raise its not-found fault.
                bool preValidated = false;
                if (_store.TryGet(entityName, id, out Entity? pre))
                {
                    FireStage(DeleteInvocation(PluginStages.PreValidation, entityName, id, pre));
                    preValidated = true;
                }

                // The store owns the cascade traversal (Restrict/RemoveLink/recursion) but delegates each actual
                // record removal to us, so the Delete plugins fire for the parent AND every cascaded child.
                _store.DeleteInternal(entityName, id, (name, recordId) => FireDeleteAndRemove(
                    name,
                    recordId,
                    firePreValidation: !(preValidated && recordId == id && string.Equals(name, entityName, StringComparison.OrdinalIgnoreCase))));
                return null;
            }, sharedVariables);
        }

        // Fires one record's Delete pre-stages, removes just that record (the store handles cascade around this),
        // then fires its post-stages — the same pre/remove/post a top-level Delete runs, applied per cascaded record.
        // The top-level record's PreValidation already ran before the cascade (see ExecuteDelete).
        private void FireDeleteAndRemove(string entityName, Guid id, bool firePreValidation)
        {
            _store.TryGet(entityName, id, out Entity? pre);

            if (firePreValidation)
            {
                FireStage(DeleteInvocation(PluginStages.PreValidation, entityName, id, pre));
            }
            FireStage(DeleteInvocation(PluginStages.PreOperation, entityName, id, pre));
            _store.RemoveRecordDirect(entityName, id);
            FireStage(DeleteInvocation(PluginStages.PostOperation, entityName, id, pre));
        }

        private static StageInvocation DeleteInvocation(PluginStages stage, string entityName, Guid id, Entity? pre)
        {
            StageInvocation invocation = NewInvocation(PluginMessages.Delete, stage, entityName, id, new EntityReference(entityName, id));
            invocation.PreState = pre;
            return invocation;
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

            // Bypass parameters apply to the request and its cascade. The effective bypass of a request is the union
            // of the bypass it inherits (from an enclosing request or batch) and its own parameters - at any depth, so
            // a plugin's nested write that sets BypassCustomPluginExecution (the standard way to avoid re-entering
            // itself) is honoured. The inherited bypass is restored once the request returns.
            BypassContext previousBypass = _bypass;
            _bypass = _bypass.Union(ExtractBypass(request));

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
                return RunOperation(() =>
                {
                    FakePluginExecutionContext context = new FakePluginExecutionContext
                    {
                        MessageName = request.RequestName,
                        Stage = (int)PluginStages.MainOperation,
                        Mode = (int)ExecutionMode.Synchronous,
                        Depth = _depth,
                        PrimaryEntityName = string.Empty,
                        CorrelationId = _correlationId,
                        IsInTransaction = IsInTransactionFor(PluginStages.MainOperation, ExecutionMode.Synchronous),
                        SharedVariables = _sharedVariables
                    };

                    foreach (KeyValuePair<string, object> parameter in request.Parameters)
                    {
                        context.InputParameters[parameter.Key] = parameter.Value;
                    }

                    IPlugin plugin = Instantiate(pluginType);

                    // The platform rejects a call that omits a required (non-optional) input parameter before the
                    // Custom API runs; enforce the same so a test cannot pass while omitting a required input.
                    if (plugin is CustomApiBase customApi && customApi.GetCustomApiDefinition() is CustomApiDefinition definition)
                    {
                        foreach (CustomApiParameterDefinition parameter in definition.InputParameters)
                        {
                            if (!parameter.IsOptional && !request.Parameters.Contains(parameter.Name))
                            {
                                throw new InvalidPluginExecutionException(
                                    $"Custom API '{request.RequestName}' requires the input parameter '{parameter.Name}', which was not supplied.");
                            }
                        }
                    }

                    _log.Add(new PipelineExecutionEntry(pluginType.FullName ?? pluginType.Name, request.RequestName, PluginStages.MainOperation, ExecutionMode.Synchronous, string.Empty, _depth));
                    ExecuteWithContext(context, plugin);

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

                case OrganizationRequest upsert when IsUpsert(upsert, out Entity? upsertTarget):
                    return ExecuteUpsert(upsert, upsertTarget!, shared);

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
                    // A key-addressed target (KeyAttributes, no Id) is resolved first so the Delete steps fire with the
                    // real record id; a key matching nothing throws the platform not-found fault.
                    ExecuteDelete(delete.Target.LogicalName, _store.ResolveReference(delete.Target), shared);
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

                case ExecuteMultipleRequest executeMultiple:
                    return ExecuteMultipleThroughPipeline(executeMultiple);

                case ExecuteTransactionRequest executeTransaction:
                    return ExecuteTransactionThroughPipeline(executeTransaction);

                default:
                    return ExecuteModelledMessage(request, shared);
            }
        }

        // Upsert is matched by RequestName so both the SDK UpsertRequest and Vela's typed UpsertRequest<T> (a plain
        // OrganizationRequest named "Upsert") run through the pipeline, as the store does.
        private static bool IsUpsert(OrganizationRequest request, out Entity? target)
        {
            target = null;
            if (string.Equals(request.RequestName, "Upsert", StringComparison.OrdinalIgnoreCase)
                && request.Parameters.TryGetValue("Target", out object? value)
                && value is Entity entity)
            {
                target = entity;
                return true;
            }

            return false;
        }

        // Upsert, mirroring Dataverse: the target is matched by its id or alternate key. A match runs the Update
        // pipeline (Update steps fire with the resolved id); no match runs the Create pipeline (Create steps fire; the
        // key columns become real columns of the new row, and a supplied id is kept). Steps registered on the Upsert
        // message itself fire around the inner operation, and the response carries RecordCreated and Target.
        private OrganizationResponse ExecuteUpsert(OrganizationRequest request, Entity target, ParameterCollection shared)
        {
            return RunOperation(() =>
            {
                FireStage(GenericInvocation("Upsert", PluginStages.PreValidation, target.LogicalName, target.Id, request.Parameters, null));
                FireStage(GenericInvocation("Upsert", PluginStages.PreOperation, target.LogicalName, target.Id, request.Parameters, null));

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
                FireStage(GenericInvocation("Upsert", PluginStages.PostOperation, target.LogicalName, affectedId, request.Parameters, response.Results));
                return (OrganizationResponse)response;
            }, shared);
        }

        // ExecuteMultiple/ExecuteTransaction must run each child through the pipeline so registered steps fire,
        // exactly as they would in production. Routing to _store.Execute (the default fall-through) would call the
        // store's own child dispatch, which bypasses the pipeline entirely and gives tests false confidence that
        // no plugin ran. Children go through ExecuteRequest so each child's effective bypass is the batch's bypass
        // united with the child's own bypass parameters.
        private ExecuteMultipleResponse ExecuteMultipleThroughPipeline(ExecuteMultipleRequest request)
        {
            bool continueOnError = request.Settings?.ContinueOnError ?? false;
            bool returnResponses = request.Settings?.ReturnResponses ?? false;
            ExecuteMultipleResponseItemCollection items = new ExecuteMultipleResponseItemCollection();
            bool faulted = false;

            for (int i = 0; i < request.Requests.Count; i++)
            {
                try
                {
                    OrganizationResponse childResponse = ExecuteRequest(request.Requests[i]);
                    if (returnResponses)
                    {
                        items.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = childResponse });
                    }
                }
                catch (Exception ex)
                {
                    faulted = true;
                    items.Add(new ExecuteMultipleResponseItem
                    {
                        RequestIndex = i,
                        Fault = ItemFault(ex),
                    });

                    if (!continueOnError)
                    {
                        break;
                    }
                }
            }

            ExecuteMultipleResponse response = new ExecuteMultipleResponse();
            response.Results["IsFaulted"] = faulted;
            response.Results["Responses"] = items;
            return response;
        }

        // Builds the per-item fault for a faulted child, mirroring the store's ExecuteMultipleItemFault: a
        // FaultException<OrganizationServiceFault> carries the platform fault (ErrorCode + Message) in its Detail, so
        // that is copied through verbatim; any other exception has no ErrorCode, so only its message is surfaced.
        private static OrganizationServiceFault ItemFault(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                return new OrganizationServiceFault
                {
                    ErrorCode = fault.Detail.ErrorCode,
                    Message = fault.Detail.Message,
                };
            }

            return new OrganizationServiceFault { Message = exception.Message };
        }

        // All-or-nothing: snapshot the store, run every child through the pipeline, and roll back the whole batch
        // if any child throws. Mirrors the store's ExecuteTransaction but with plugin steps firing per child.
        // The batch is one transaction, so the children's async steps wait for the whole batch to commit and are
        // dropped with it.
        private ExecuteTransactionResponse ExecuteTransactionThroughPipeline(ExecuteTransactionRequest request)
        {
            OrganizationResponseCollection responses = new OrganizationResponseCollection();

            RunAtomically(() =>
            {
                foreach (OrganizationRequest child in request.Requests)
                {
                    OrganizationResponse childResponse = ExecuteRequest(child);
                    if (request.ReturnResponses == true)
                    {
                        responses.Add(childResponse);
                    }
                }

                return true;
            }, transactional: true);

            ExecuteTransactionResponse response = new ExecuteTransactionResponse();
            response.Results["Responses"] = responses;
            return response;
        }

        // A message with no dedicated data simulation but with registered steps (for example QualifyLead,
        // WinOpportunity, AddToQueue, SendEmail, CloseIncident, or the sharing messages) fires its Pre/Post steps
        // like any other plugin: the request parameters become InputParameters (so ctx.Request is fully typed)
        // and the response Results become OutputParameters in the Post stage. The data effect itself is not
        // simulated - a fake cannot reproduce the server logic - so the store is asked to execute only if it
        // recognises the request; otherwise the main operation is a no-op returning an empty response. A message
        // with no registered steps falls through to the store unchanged (metadata, file protocol, or its own
        // "unsupported message" error).
        private OrganizationResponse ExecuteModelledMessage(OrganizationRequest request, ParameterCollection shared)
        {
            // Steps register on the sdkmessage, which differs from the request name for some business messages
            // (SendEmailRequest runs the Send steps, WinQuoteRequest the Win steps), and plugins see that name.
            string message = SdkMessageNames.ToSdkMessage(request.RequestName);
            bool hasSteps = _registrations.Any(registration => registration.Step.Enabled
                && string.Equals(registration.Step.Message, message, StringComparison.OrdinalIgnoreCase));
            if (!hasSteps)
            {
                return _store.Execute(request);
            }

            (string entityName, Guid entityId) = DerivePrimaryEntity(request);

            // Retrieve and RetrieveMultiple are not transactional (verified live: IsInTransaction false at stages 20
            // and 40); every other message runs its main operation in a transaction.
            bool transactional = !NonTransactionalMessages.Contains(message);
            return RunOperation(() =>
            {
                FireStage(GenericInvocation(message, PluginStages.PreValidation, entityName, entityId, request.Parameters, null));
                FireStage(GenericInvocation(message, PluginStages.PreOperation, entityName, entityId, request.Parameters, null));

                OrganizationResponse response = TryStoreExecute(request) ?? new OrganizationResponse { ResponseName = request.RequestName };

                FireStage(GenericInvocation(message, PluginStages.PostOperation, entityName, entityId, request.Parameters, response.Results));
                return response;
            }, shared, transactional);
        }

        private static readonly HashSet<string> NonTransactionalMessages =
            new HashSet<string>(new[] { "Retrieve", "RetrieveMultiple" }, StringComparer.OrdinalIgnoreCase);

        private static StageInvocation GenericInvocation(string message, PluginStages stage, string entityName, Guid entityId, ParameterCollection inputs, ParameterCollection? outputs)
        {
            return new StageInvocation
            {
                Message = message,
                Stage = stage,
                EntityLogicalName = entityName,
                PrimaryId = entityId,
                AllInputs = inputs,
                Outputs = outputs,
            };
        }

        // Best-effort primary entity for a generic message: an explicit Target, else the first EntityReference /
        // Entity parameter (covers LeadId, Target, EntityMoniker, ...). When nothing resolves, the steps match by
        // (message, stage) alone.
        private static readonly (string Parameter, string Lookup)[] ClosingActivityPrimaries =
        {
            ("OpportunityClose", "opportunityid"),
            ("QuoteClose", "quoteid"),
            ("IncidentResolution", "incidentid"),
        };

        private static (string, Guid) DerivePrimaryEntity(OrganizationRequest request)
        {
            if (request.Parameters.TryGetValue("Target", out object? target))
            {
                if (target is Entity targetEntity)
                {
                    return (targetEntity.LogicalName, targetEntity.Id);
                }

                if (target is EntityReference targetReference)
                {
                    return (targetReference.LogicalName, targetReference.Id);
                }
            }

            // Messages whose primary record is not the first entity-typed parameter: a closing activity whose lookup
            // points at the real primary. The generic scan below would otherwise pick the activity
            // (opportunityclose, incidentresolution, quoteclose) and an entity-filtered step would silently not fire
            // (A15). Verified live: Win/Lose run with PrimaryEntityName opportunity, Close with incident.
            foreach ((string parameterName, string lookup) in ClosingActivityPrimaries)
            {
                if (request.Parameters.TryGetValue(parameterName, out object? activity)
                    && activity is Entity activityEntity
                    && activityEntity.Attributes.TryGetValue(lookup, out object? primary)
                    && primary is EntityReference primaryRef)
                {
                    return (primaryRef.LogicalName, primaryRef.Id);
                }
            }

            foreach (KeyValuePair<string, object> parameter in request.Parameters)
            {
                if (parameter.Value is EntityReference reference)
                {
                    return (reference.LogicalName, reference.Id);
                }

                if (parameter.Value is Entity entity)
                {
                    return (entity.LogicalName, entity.Id);
                }
            }

            return (string.Empty, Guid.Empty);
        }

        // The store simulates a subset of messages (grant/modify/revoke access, sharing reads, ...). For a message
        // it does not model it throws "unsupported"; the generic path still fires the steps around a no-op.
        private OrganizationResponse? TryStoreExecute(OrganizationRequest request)
        {
            try
            {
                return _store.Execute(request);
            }
            catch (InvalidPluginExecutionException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
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
            RunOperation<object?>(() =>
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
                    FireStage(preInvocation);
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
                FireStage(postInvocation);

                return null;
            }, sharedVariables);
        }

        private T RunOperation<T>(Func<T> body, ParameterCollection? sharedVariables = null, bool transactional = true)
        {
            _depth++;
            ParameterCollection previousShared = _sharedVariables;
            bool previousEnclosed = _operationEnclosedInTransaction;
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

                _operationEnclosedInTransaction = _openTransactions > 0;
                return RunAtomically(body, transactional);
            }
            finally
            {
                _operationEnclosedInTransaction = previousEnclosed;
                _sharedVariables = previousShared;
                _depth--;
            }
        }

        // Runs the body all-or-nothing: if it throws, the store is restored and the async steps queued inside it are
        // dropped. The async steps run only once the outermost scope has committed.
        private T RunAtomically<T>(Func<T> body, bool transactional)
        {
            bool outermost = _openScopes == 0;
            if (outermost)
            {
                _correlationId = Guid.NewGuid();
            }

            var snapshot = _store.Snapshot();
            int queued = _pendingAsyncSteps.Count;
            _openScopes++;
            if (transactional)
            {
                _openTransactions++;
            }

            T result;
            try
            {
                result = body();
            }
            catch
            {
                _store.RestoreTo(snapshot);
                _pendingAsyncSteps.RemoveRange(queued, _pendingAsyncSteps.Count - queued);
                throw;
            }
            finally
            {
                _openScopes--;
                if (transactional)
                {
                    _openTransactions--;
                }
            }

            if (outermost)
            {
                RunPendingAsyncSteps();
            }

            return result;
        }

        // Queues an async step with the state it was triggered in, to run once the outermost scope commits.
        private void QueueAsyncStep(Action dispatch)
        {
            _pendingAsyncSteps.Add(new PendingAsyncStep
            {
                Dispatch = dispatch,
                Depth = _depth,
                SharedVariables = _sharedVariables,
                Bypass = _bypass,
                CallerUserId = _callerUserId,
                InitiatingUserId = _initiatingUserId,
                CorrelationId = _correlationId,
                RequestId = _requestId,
                ParentContext = _contextStack.Count > 0 ? _contextStack.Peek() : null,
            });
        }

        // Runs the queued async steps in order. A step's own writes may queue more, which run after it. Real Dataverse
        // runs async steps as decoupled system jobs whose failures never surface to the triggering caller, so any
        // exception is recorded (assert via AsyncStepFailures) instead of thrown out of the originating operation.
        private void RunPendingAsyncSteps()
        {
            while (_pendingAsyncSteps.Count > 0)
            {
                PendingAsyncStep step = _pendingAsyncSteps[0];
                _pendingAsyncSteps.RemoveAt(0);
                RunAsyncStep(step);
            }
        }

        // Runs one async step as its own system job: outside any transaction, in its own rollback scope (a failure
        // undoes its writes and drops the async steps they queued), and with the state captured when it was queued.
        private void RunAsyncStep(PendingAsyncStep step)
        {
            int previousDepth = _depth;
            ParameterCollection previousShared = _sharedVariables;
            BypassContext previousBypass = _bypass;
            Guid? previousCaller = _callerUserId;
            Guid? previousInitiating = _initiatingUserId;
            Guid previousCorrelation = _correlationId;
            Guid previousRequest = _requestId;

            _depth = step.Depth;
            _sharedVariables = step.SharedVariables;
            _bypass = step.Bypass;
            _callerUserId = step.CallerUserId;
            _initiatingUserId = step.InitiatingUserId;
            _correlationId = step.CorrelationId;
            _requestId = step.RequestId;
            _asyncOperationId = Guid.NewGuid();
            if (step.ParentContext != null)
            {
                _contextStack.Push(step.ParentContext);
            }

            var snapshot = _store.Snapshot();
            int queued = _pendingAsyncSteps.Count;
            _openScopes++;
            try
            {
                if (step.CallerUserId is Guid caller)
                {
                    _store.RunAs(caller == SystemUserId ? (Guid?)null : caller, () =>
                    {
                        step.Dispatch();
                        return true;
                    });
                }
                else
                {
                    step.Dispatch();
                }
            }
            catch (Exception ex)
            {
                _store.RestoreTo(snapshot);
                _pendingAsyncSteps.RemoveRange(queued, _pendingAsyncSteps.Count - queued);
                _asyncStepFailures.Add(ex);
            }
            finally
            {
                _openScopes--;
                if (step.ParentContext != null)
                {
                    _contextStack.Pop();
                }

                _depth = previousDepth;
                _sharedVariables = previousShared;
                _bypass = previousBypass;
                _callerUserId = previousCaller;
                _initiatingUserId = previousInitiating;
                _correlationId = previousCorrelation;
                _requestId = previousRequest;
            }
        }

        // Live, an async step runs outside any transaction. A PreValidation step runs before its operation's
        // transaction begins, so it is in one only when its operation is nested in another's transaction (verified
        // live: false at depth 1, true nested). Every other stage runs in the operation's transaction, which the
        // non-transactional reads do not open.
        private bool IsInTransactionFor(PluginStages stage, ExecutionMode mode)
        {
            if (mode == ExecutionMode.Asynchronous)
            {
                return false;
            }

            return stage == PluginStages.PreValidation ? _operationEnclosedInTransaction : _openTransactions > 0;
        }

        private void FireStage(StageInvocation invocation)
        {
            List<PipelineRegistration> steps = _registrations
                // A disabled step (.Disabled()) is registered but dormant: it never fires, as on a real org.
                .Where(registration => registration.Step.Enabled
                    && registration.Step.Stage == invocation.Stage
                    && string.Equals(registration.Step.Message, invocation.Message, StringComparison.OrdinalIgnoreCase)
                    // A generic modelled message may not resolve a primary entity from the request; match its
                    // steps by (message, stage) alone in that case. Likewise an entity-none step (empty logical
                    // name, an intentionally global step) matches any entity, and a global message
                    // (Associate/Disassociate: sdkmessagefilter "none") fires regardless of the entity the step was
                    // declared on - mirroring the runtime's ResolveSingleMatchingStep and the CLI's registration.
                    && (string.IsNullOrEmpty(invocation.EntityLogicalName)
                        || PluginMessages.IsGlobalMessage(invocation.Message)
                        || string.IsNullOrEmpty(registration.Step.EntityLogicalName)
                        || string.Equals(registration.Step.EntityLogicalName, invocation.EntityLogicalName, StringComparison.OrdinalIgnoreCase)))
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
                    QueueAsyncStep(() => Dispatch(captured, invocation));
                }
                else
                {
                    Dispatch(captured, invocation);
                }
            }
        }

        // Fires one stage of a Create/Update batch. The two registration messages are merged: a bulk step
        // (CreateMultiple/UpdateMultiple) fires once with the whole Targets collection; a single step
        // (Create/Update) fires once per row. Both are ordered by execution order and filtered by FilterOn.
        private void FireBatchStage(string singleMessage, string bulkMessage, PluginStages stage, string entityName, IReadOnlyList<BatchItem> items)
        {
            bool isUpdate = string.Equals(singleMessage, PluginMessages.Update, StringComparison.OrdinalIgnoreCase);

            List<PipelineRegistration> matching = _registrations
                .Where(registration => registration.Step.Enabled
                    && registration.Step.Stage == stage
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
                    if (!items.Any(item => FiltersMatch(captured.Step, item.ChangedAttributes)))
                    {
                        continue;
                    }

                    EntityCollection capturedTargets = targets ??= BuildTargets(entityName, items);
                    Emit(captured, () => DispatchBulk(captured, bulkMessage, stage, entityName, capturedTargets, items));
                }
                else
                {
                    foreach (BatchItem item in items)
                    {
                        if (!FiltersMatch(captured.Step, item.ChangedAttributes))
                        {
                            continue;
                        }

                        BatchItem capturedItem = item;
                        StageInvocation invocation = NewInvocation(singleMessage, stage, entityName, capturedItem.Target.Id, capturedItem.Target);
                        invocation.PreState = capturedItem.PreState;
                        invocation.PostState = capturedItem.PostState;
                        invocation.ChangedAttributes = capturedItem.ChangedAttributes;
                        // A Create Post step sees the created id as OutputParameters["id"], exactly as Dataverse
                        // surfaces it, so ctx.Response.id resolves in a post-create handler.
                        if (!isUpdate && stage == PluginStages.PostOperation)
                        {
                            invocation.Outputs = new ParameterCollection { { "id", capturedItem.Target.Id } };
                        }
                        Emit(captured, () => Dispatch(captured, invocation));
                    }
                }
            }
        }

        // Runs the dispatch now for a synchronous step, or defers it to after the synchronous commit for an
        // asynchronous one — the same sync/async split FireStage applies to single-operation steps.
        private void Emit(PipelineRegistration registration, Action dispatch)
        {
            if (registration.Step.Mode == ExecutionMode.Asynchronous)
            {
                QueueAsyncStep(dispatch);
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
                IsInTransaction = IsInTransactionFor(stage, registration.Step.Mode),
                SharedVariables = _sharedVariables
            };

            // Bulk steps read the batch via ctx.BulkTargets (InputParameters["Targets"]) and per-row pre/post
            // images via the image collections (aligned by target index).
            context.InputParameters["Targets"] = targets;
            context.PreEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PreImages, item.PreState)).ToArray();
            context.PostEntityImagesCollection = items.Select(item => BuildImageCollection(registration.Step.PostImages, item.PostState)).ToArray();

            // A CreateMultiple Post step sees the created ids as OutputParameters["Ids"], exactly as Dataverse
            // surfaces them, so ctx.Response.Ids resolves in a post-create-multiple handler.
            if (string.Equals(message, PluginMessages.CreateMultiple, StringComparison.OrdinalIgnoreCase)
                && stage == PluginStages.PostOperation)
            {
                context.OutputParameters["Ids"] = targets.Entities.Select(entity => entity.Id).ToArray();
            }

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, message, stage, registration.Step.Mode, entityName, _depth));
            IPlugin plugin = registration.CreateInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, registration.Step);
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
                // When the invocation could not resolve a primary entity (a generic message with no Target), use
                // the step's own entity so the plugin's own step matching (which compares PrimaryEntityName to the
                // step's EntityLogicalName) still resolves this step.
                PrimaryEntityName = string.IsNullOrEmpty(invocation.EntityLogicalName)
                    ? registration.Step.EntityLogicalName
                    : invocation.EntityLogicalName,
                PrimaryEntityId = invocation.PrimaryId,
                CorrelationId = _correlationId,
                IsInTransaction = IsInTransactionFor(invocation.Stage, registration.Step.Mode),
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

            // Generic modelled message: surface the whole request as InputParameters (so ctx.Request is fully
            // typed) and, in the Post stage, the response Results as OutputParameters (so ctx.Response works).
            if (invocation.AllInputs != null)
            {
                foreach (KeyValuePair<string, object> input in invocation.AllInputs)
                {
                    context.InputParameters[input.Key] = input.Value;
                }
            }

            if (invocation.Outputs != null)
            {
                foreach (KeyValuePair<string, object> output in invocation.Outputs)
                {
                    context.OutputParameters[output.Key] = output.Value;
                }
            }

            BuildImages(context.PreEntityImages, registration.Step.PreImages, invocation.PreState);
            BuildImages(context.PostEntityImages, registration.Step.PostImages, invocation.PostState);

            _log.Add(new PipelineExecutionEntry(registration.Step.PluginTypeName, invocation.Message, invocation.Stage, registration.Step.Mode, invocation.EntityLogicalName, _depth));
            IPlugin plugin = registration.CreateInstance(SecureConfigFor(registration.Step));
            ExecuteWithContext(context, plugin, registration.Step);
        }

        private string? SecureConfigFor(StepDefinition step)
        {
            return !string.IsNullOrEmpty(step.SecureConfigName) && _secureConfigs.TryGetValue(step.SecureConfigName, out string? value)
                ? value
                : null;
        }

        // Runs a call as the identity of the service that issued it (see PipelineOrganizationService): the store
        // enforces security as that user (or not at all for SYSTEM), and plugins fired by the call see that user as
        // ctx.UserId. The test-facing service (Caller) keeps whatever identity is in force. The outermost caller is
        // remembered as the initiating user, which never changes through impersonation or SYSTEM calls. A call that
        // enters the pipeline from outside (no request in progress) starts a new request id.
        internal T RunAs<T>(PipelineIdentity identity, Func<T> body)
        {
            if (_inRequest)
            {
                return RunAsCore(identity, body);
            }

            _inRequest = true;
            _requestId = Guid.NewGuid();
            try
            {
                return RunAsCore(identity, body);
            }
            finally
            {
                _inRequest = false;
            }
        }

        private T RunAsCore<T>(PipelineIdentity identity, Func<T> body)
        {
            if (identity.IsCaller)
            {
                return body();
            }

            bool system = identity.IsSystem || identity.UserId == SystemUserId;
            Guid? previousCaller = _callerUserId;
            Guid? previousInitiating = _initiatingUserId;
            _initiatingUserId ??= _callerUserId ?? _store.CurrentActor().UserId;
            _callerUserId = system ? SystemUserId : identity.UserId;
            try
            {
                return _store.RunAs(system ? (Guid?)null : identity.UserId, body);
            }
            finally
            {
                _callerUserId = previousCaller;
                _initiatingUserId = previousInitiating;
            }
        }

        // Finalizes a freshly built context and runs the plugin. Stamps the calling identity (so ctx.UserId/
        // InitiatingUserId/BusinessUnitId/OrganizationId match WhoAmI and system-field stamping instead of being
        // random): UserId is the step's impersonated user (WithImpersonation(Guid)) when set, otherwise the identity
        // of the service that issued the triggering call; InitiatingUserId is always the outermost caller. Links the
        // ParentContext for a cascaded (nested) execution, and keeps the context on the stack while the plugin runs
        // so a write it triggers sees it as the parent.
        private void ExecuteWithContext(FakePluginExecutionContext context, IPlugin plugin, StepDefinition? step = null)
        {
            (Guid actingUserId, Guid businessUnitId, Guid organizationId) = _store.CurrentActor();
            Guid caller = _callerUserId ?? actingUserId;
            Guid userId = step?.ImpersonatingUserId is Guid impersonated && impersonated != Guid.Empty ? impersonated : caller;
            if (userId != caller && userId != SystemUserId)
            {
                // The impersonated user's own business unit (from its security registration, when configured).
                businessUnitId = _store.RunAs<Guid>(userId, () => _store.CurrentActor().BusinessUnitId);
            }

            context.UserId = userId;
            context.InitiatingUserId = _initiatingUserId ?? actingUserId;
            context.BusinessUnitId = businessUnitId;
            context.OrganizationId = organizationId;
            context.ParentContext = _contextStack.Count > 0 ? _contextStack.Peek() : null;

            // Verified live: every step reports the sandbox isolation mode (2) and "none" as SecondaryEntityName.
            context.RequestId = _requestId;
            context.OperationId = context.Mode == (int)ExecutionMode.Asynchronous ? _asyncOperationId : _requestId;
            context.IsolationMode = 2;
            context.SecondaryEntityName = "none";

            _contextStack.Push(context);
            try
            {
                plugin.Execute(BuildProvider(context));
            }
            finally
            {
                _contextStack.Pop();
            }
        }

        private IServiceProvider BuildProvider(FakePluginExecutionContext context)
        {
            // ctx.Service is CreateOrganizationService(ctx.UserId): a service acting as that user (subject to the
            // security model). ctx.SystemService is CreateOrganizationService(null): SYSTEM, not subject to it.
            IOrganizationServiceFactory factory = new PipelineServiceFactory(this);

            Dictionary<Type, object> services = new Dictionary<Type, object>
            {
                [typeof(IPluginExecutionContext)] = context,
                [typeof(IOrganizationServiceFactory)] = factory,
                [typeof(ITracingService)] = _trace,
                [typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger)] = _logger
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

        // Hands plugins identity-bound pipeline services: null (or SystemUserId) -> SYSTEM, any other id -> that user.
        private sealed class PipelineServiceFactory : IOrganizationServiceFactory
        {
            private readonly PluginPipeline _pipeline;

            public PipelineServiceFactory(PluginPipeline pipeline)
            {
                _pipeline = pipeline;
            }

            public IOrganizationService CreateOrganizationService(Guid? userId)
            {
                return userId == null || userId.Value == SystemUserId
                    ? new PipelineOrganizationService(_pipeline, PipelineIdentity.System)
                    : new PipelineOrganizationService(_pipeline, PipelineIdentity.User(userId.Value));
            }
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

            // The directives of both contexts: a nested request keeps everything it inherited and adds its own.
            public BypassContext Union(BypassContext other)
            {
                if (!other.AllCustom && !other.BypassSync && !other.BypassAsync && other.StepKeys.Count == 0)
                {
                    return this;
                }

                BypassContext union = new BypassContext
                {
                    AllCustom = AllCustom || other.AllCustom,
                    BypassSync = BypassSync || other.BypassSync,
                    BypassAsync = BypassAsync || other.BypassAsync,
                };
                union.StepKeys.UnionWith(StepKeys);
                union.StepKeys.UnionWith(other.StepKeys);
                return union;
            }

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

        // An async step waiting for the outermost scope to commit, with the state it was triggered in.
        private sealed class PendingAsyncStep
        {
            public Action Dispatch = null!;
            public int Depth;
            public ParameterCollection SharedVariables = null!;
            public BypassContext Bypass = null!;
            public Guid? CallerUserId;
            public Guid? InitiatingUserId;
            public Guid CorrelationId;
            public Guid RequestId;
            public FakePluginExecutionContext? ParentContext;
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
            // For a generic modelled message (no dedicated data simulation): the whole request parameter set to
            // expose as InputParameters, and the response Results to expose as OutputParameters (Post stage).
            public ParameterCollection? AllInputs;
            public ParameterCollection? Outputs;
        }
    }
}
