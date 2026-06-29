using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// A stateful in-memory <see cref="IOrganizationService"/> test double. Stores records in a dictionary
    /// so Create/Retrieve/Update/Delete round-trips work without scripting each call. <see cref="RetrieveMultiple"/>
    /// supports <see cref="QueryExpression"/>, <see cref="QueryByAttribute"/> and <see cref="FetchExpression"/>
    /// (FetchXML, common subset) with: filters (And/Or, many operators incl. Between, Begins/Ends/Like, the
    /// date operators, EqualUserId and CompareColumns), conditions targeting linked entities
    /// (ConditionExpression.EntityName), link-entities (Inner/LeftOuter/Exists/In/MatchFirstRowUsingCrossApply,
    /// aliased columns, link criteria, nested links), Any/All/NotAny/NotAll related-record filters, ordering
    /// (incl. linked columns), TopCount, paging, Distinct, total record count, column aliases and aggregates
    /// (count/sum/avg/min/max + group-by, via FetchXML or ColumnSet.AttributeExpressions). For anything beyond
    /// that, inject your own <see cref="IOrganizationService"/> via <c>WithOrganizationService</c>.
    /// </summary>
    /// <remarks>
    /// This type owns the store and the platform-behaviour configuration (alternate keys, auto-numbers). The
    /// read side (query execution) lives in <see cref="InMemoryQueryEngine"/>; scalar value handling shared by
    /// both is in <see cref="EntityValues"/>.
    /// </remarks>
    public sealed class InMemoryOrganizationService : IOrganizationService
    {
        private readonly Dictionary<string, Dictionary<Guid, Entity>> _store =
            new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);

        // Platform behaviours are metadata-driven, so they are opt-in: a test declares them explicitly.
        private readonly AlternateKeyValidator _alternateKeys = new AlternateKeyValidator();

        private readonly AutoNumberApplier _autoNumbers = new AutoNumberApplier();

        private readonly SystemFieldApplier _systemFields = new SystemFieldApplier();

        // Virtual tables declared read-only: Create/Update/Delete are rejected, like a read-only external provider.
        private readonly HashSet<string> _readOnlyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly RelationshipRegistry _relationships = new RelationshipRegistry();

        private readonly Dictionary<string, EntityMetadata> _metadata =
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stable, non-empty calling user used when <see cref="WithCurrentUser"/> is not set, so audit/owner
        /// columns and <c>EqualUserId</c> filters resolve to a real user instead of the empty GUID.
        /// </summary>
        public static readonly Guid DefaultCallingUser = new Guid("a11ad0c0-0000-0000-0000-000000000001");

        private Guid _currentUserId = DefaultCallingUser;

        private Guid _currentBusinessUnitId = Guid.Empty;

        private Guid _currentOrganizationId = Guid.Empty;

        private Func<DateTime> _clock = () => DateTime.UtcNow;

        private Func<Guid> _idSequence = () => Guid.NewGuid();

        // Monotonic row version (versionnumber) bumped on every Create/Update, like the platform's rowversion.
        private long _version;

        // File/Image column block protocol: committed content keyed by fileid, plus in-flight upload/download
        // sessions keyed by their continuation token. A File column stores its fileid (a Guid); the content
        // lives here and is fetched through the block messages, mirroring the platform.
        private readonly Dictionary<Guid, FileBlob> _files = new Dictionary<Guid, FileBlob>();
        private readonly Dictionary<string, UploadSession> _uploads = new Dictionary<string, UploadSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _downloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private int _tokenSequence;

        /// <summary>
        /// Sets the calling user: matched by the <c>EqualUserId</c>/<c>NotEqualUserId</c> condition operators and
        /// stamped into the <c>createdby</c>/<c>modifiedby</c>/<c>ownerid</c> system columns on Create/Update.
        /// </summary>
        public InMemoryOrganizationService WithCurrentUser(Guid userId)
        {
            _currentUserId = userId;
            return this;
        }

        /// <summary>Sets the business unit stamped into <c>owningbusinessunit</c> on user/team-owned Create/Update.</summary>
        public InMemoryOrganizationService WithCurrentBusinessUnit(Guid businessUnitId)
        {
            _currentBusinessUnitId = businessUnitId;
            return this;
        }

        /// <summary>Sets the organization id stamped into <c>organizationid</c> on organization-owned Create.</summary>
        public InMemoryOrganizationService WithOrganizationId(Guid organizationId)
        {
            _currentOrganizationId = organizationId;
            return this;
        }

        /// <summary>
        /// Overrides the clock used to stamp <c>createdon</c>/<c>modifiedon</c> (default <see cref="DateTime.UtcNow"/>).
        /// Pin it to a fixed value to assert timestamps deterministically.
        /// </summary>
        public InMemoryOrganizationService WithClock(Func<DateTime> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            return this;
        }

        /// <summary>
        /// Overrides how ids are generated for records created without one (default <see cref="Guid.NewGuid"/>),
        /// so generated ids are reproducible. Companion to <see cref="WithClock"/>.
        /// </summary>
        public InMemoryOrganizationService WithIdSequence(Func<Guid> sequence)
        {
            _idSequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
            return this;
        }

        /// <summary>
        /// Installs a deterministic, sequential id generator (<c>00000000-0000-0000-0000-000000000001</c>,
        /// <c>...0002</c>, …) for records created without an id. Assert expected ids with <see cref="SequentialIds.At"/>.
        /// </summary>
        public InMemoryOrganizationService WithIdSequence()
        {
            return WithIdSequence(SequentialIds.Starting());
        }

        /// <summary>
        /// Declares a table as organization-owned: Create/Update stamp audit columns
        /// (<c>createdon</c>/<c>createdby</c>/<c>modifiedon</c>/<c>modifiedby</c>) but no owner columns.
        /// </summary>
        public InMemoryOrganizationService AsOrganizationOwned(string entityLogicalName)
        {
            _systemFields.Declare(entityLogicalName, OwnershipKind.OrganizationOwned);
            return this;
        }

        /// <summary>
        /// Declares a table as user/team-owned (the default): Create/Update stamp audit columns plus
        /// <c>ownerid</c>, <c>owninguser</c>/<c>owningteam</c> and <c>owningbusinessunit</c>. Use this to override
        /// inference for a late-bound table that should carry owner columns.
        /// </summary>
        public InMemoryOrganizationService AsUserOwned(string entityLogicalName)
        {
            _systemFields.Declare(entityLogicalName, OwnershipKind.UserOwned);
            return this;
        }

        /// <summary>Declares a table as a virtual (external-data) table: no system columns are auto-populated.</summary>
        public InMemoryOrganizationService AsVirtual(string entityLogicalName) => AsVirtual(entityLogicalName, readOnly: false);

        /// <summary>
        /// Declares a virtual (external-data) table, optionally <paramref name="readOnly"/> so Create/Update/Delete
        /// are rejected — modelling a read-only external provider.
        /// </summary>
        public InMemoryOrganizationService AsVirtual(string entityLogicalName, bool readOnly)
        {
            _systemFields.Declare(entityLogicalName, OwnershipKind.Virtual);
            if (readOnly)
            {
                _readOnlyTables.Add(entityLogicalName);
            }

            return this;
        }

        /// <summary>
        /// Declares a table as an elastic table. Elastic is a storage type, not an ownership type — elastic
        /// tables are ownable, so they carry the same audit and owner columns as a user/team-owned table.
        /// </summary>
        public InMemoryOrganizationService AsElastic(string entityLogicalName)
        {
            _systemFields.Declare(entityLogicalName, OwnershipKind.Elastic);
            return this;
        }

        /// <summary>
        /// Declares an alternate key (one or more attributes) whose combined value must be unique. Create/Update
        /// throw when another record carries the same populated key, mirroring the platform's duplicate-key fault.
        /// </summary>
        public InMemoryOrganizationService WithAlternateKey(string entityLogicalName, params string[] keyAttributes)
        {
            _alternateKeys.Add(entityLogicalName, keyAttributes);
            return this;
        }

        /// <summary>
        /// Declares a many-to-many relationship so <see cref="Associate"/>/<see cref="Disassociate"/> can create and
        /// delete its intersect rows. <paramref name="firstAttribute"/>/<paramref name="secondAttribute"/> are the
        /// intersect entity's two lookup columns, each tied to its table. For example:
        /// <c>WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")</c>.
        /// </summary>
        public InMemoryOrganizationService WithManyToMany(string relationshipSchemaName, string intersectEntity, string firstEntity, string firstAttribute, string secondEntity, string secondAttribute)
        {
            _relationships.Add(relationshipSchemaName, intersectEntity, firstEntity, firstAttribute, secondEntity, secondAttribute);
            return this;
        }

        /// <summary>
        /// Strongly-typed <see cref="WithManyToMany(string, string, string, string, string, string)"/>: the intersect
        /// table and the two side tables come from the early-bound types, and the two lookup columns from the
        /// property accesses. For example, for an <c>account_contact</c> intersect:
        /// <c>WithManyToMany&lt;AccountContact, Account, Contact&gt;("account_contacts", ac =&gt; ac.AccountId, ac =&gt; ac.ContactId)</c>.
        /// </summary>
        public InMemoryOrganizationService WithManyToMany<TIntersect, TFirst, TSecond>(
            string relationshipSchemaName,
            Expression<Func<TIntersect, object?>> firstAttribute,
            Expression<Func<TIntersect, object?>> secondAttribute)
            where TIntersect : Entity
            where TFirst : Entity
            where TSecond : Entity
        {
            return WithManyToMany(
                relationshipSchemaName,
                EarlyBound.EntityLogicalName<TIntersect>(),
                EarlyBound.EntityLogicalName<TFirst>(),
                EarlyBound.AttributeLogicalName(firstAttribute),
                EarlyBound.EntityLogicalName<TSecond>(),
                EarlyBound.AttributeLogicalName(secondAttribute));
        }

        /// <summary>
        /// Registers table metadata so plugins can read it with <c>RetrieveEntityRequest</c>,
        /// <c>RetrieveAllEntitiesRequest</c> and <c>RetrieveAttributeRequest</c>. Build metadata easily with
        /// <see cref="FakeEntityMetadata"/>. The in-memory store has no metadata of its own, so only what you
        /// register is returned.
        /// </summary>
        public InMemoryOrganizationService WithEntityMetadata(params EntityMetadata[] metadata)
        {
            return WithEntityMetadataCore(metadata);
        }

        /// <summary>
        /// Registers table metadata auto-derived from an early-bound type generated by <c>vela models</c> — no
        /// hand-written <see cref="FakeEntityMetadata"/>, no connection. Shorthand for
        /// <c>WithEntityMetadata(FakeEntityMetadata.From&lt;TEntity&gt;().Build())</c>; use the non-generic
        /// overload with <see cref="FakeEntityMetadata.From{TEntity}"/> when you need to chain overrides.
        /// </summary>
        public InMemoryOrganizationService WithEntityMetadata<TEntity>() where TEntity : Entity
        {
            return WithEntityMetadataCore(new[] { FakeEntityMetadata.From<TEntity>().Build() });
        }

        private InMemoryOrganizationService WithEntityMetadataCore(EntityMetadata[] metadata)
        {
            foreach (EntityMetadata entity in metadata)
            {
                string? logicalName = entity?.LogicalName;
                if (string.IsNullOrWhiteSpace(logicalName))
                {
                    throw new ArgumentException("Entity metadata must have a LogicalName (use FakeEntityMetadata.For(...)).", nameof(metadata));
                }

                _metadata[logicalName!] = entity!;
            }

            return this;
        }

        /// <summary>
        /// Declares an auto-number attribute. On Create, when the attribute is absent or empty, the generator is
        /// invoked with a 1-based sequence number to populate it (e.g. <c>n =&gt; $"ACC-{n:0000}"</c>).
        /// </summary>
        public InMemoryOrganizationService WithAutoNumber(string entityLogicalName, string attribute, Func<int, object> generator)
        {
            _autoNumbers.Add(entityLogicalName, attribute, generator);
            return this;
        }

        /// <summary>
        /// Declares a sequential auto-number attribute: <c>{prefix}{paddedNumber}{suffix}</c>. For example
        /// <c>WithAutoNumber("account", "accountnumber", "ACC-", 5)</c> yields <c>ACC-00001</c>, <c>ACC-00002</c>, …
        /// </summary>
        public InMemoryOrganizationService WithAutoNumber(string entityLogicalName, string attribute, string prefix, int length, string suffix = "")
        {
            return WithAutoNumber(entityLogicalName, attribute, AutoNumber.Sequential(prefix, length, suffix));
        }

        /// <summary>Pre-populates the store without going through Create (ids are assigned when empty).</summary>
        public InMemoryOrganizationService Seed(params Entity[] entities)
        {
            foreach (Entity entity in entities)
            {
                Entity stored = EntityValues.Clone(entity);
                if (stored.Id == Guid.Empty)
                {
                    stored.Id = _idSequence();
                }

                Table(stored.LogicalName)[stored.Id] = stored;
            }

            return this;
        }

        /// <summary>Returns a snapshot (clones) of all stored records of the given logical name.</summary>
        public IReadOnlyList<Entity> GetAll(string logicalName)
        {
            return Table(logicalName).Values.Select(EntityValues.Clone).ToList();
        }

        public Guid Create(Entity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            EnsureWritable(entity.LogicalName);
            Entity stored = EntityValues.Clone(entity);
            if (stored.Id == Guid.Empty)
            {
                stored.Id = _idSequence();
            }

            _autoNumbers.Apply(stored);
            _systemFields.ApplyOnCreate(entity, stored, Actor(), _clock(), NextVersion());
            _alternateKeys.Enforce(stored, Table(stored.LogicalName).Values);
            Table(stored.LogicalName)[stored.Id] = stored;
            return stored.Id;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            if (!Table(entityName).TryGetValue(id, out Entity? stored))
            {
                throw new InvalidOperationException($"{entityName} with id {id} does not exist in the in-memory store.");
            }

            return Project(stored, columnSet);
        }

        public void Update(Entity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            EnsureWritable(entity.LogicalName);
            if (!Table(entity.LogicalName).TryGetValue(entity.Id, out Entity? stored))
            {
                throw new InvalidOperationException($"{entity.LogicalName} with id {entity.Id} does not exist in the in-memory store.");
            }

            // Validate the would-be merged record before committing so a duplicate-key throw leaves the store intact.
            Entity candidate = EntityValues.Clone(stored);
            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                candidate[attribute.Key] = EntityValues.CloneValue(attribute.Value);
            }

            _alternateKeys.Enforce(candidate, Table(entity.LogicalName).Values);

            // Dataverse Update merges the supplied attributes onto the existing record. Deep-copy the incoming
            // values so the caller's entity does not stay aliased with the store after the call returns.
            foreach (KeyValuePair<string, object> attribute in entity.Attributes)
            {
                stored[attribute.Key] = EntityValues.CloneValue(attribute.Value);
            }

            _systemFields.ApplyOnUpdate(entity, stored, Actor(), _clock(), NextVersion());
        }

        private SystemActor Actor() => new SystemActor(_currentUserId, _currentBusinessUnitId, _currentOrganizationId);

        private long NextVersion() => ++_version;

        private void EnsureWritable(string logicalName)
        {
            if (_readOnlyTables.Contains(logicalName))
            {
                throw new InvalidOperationException(
                    $"'{logicalName}' is a read-only virtual table; writes are rejected (declared with AsVirtual(\"{logicalName}\", readOnly: true)).");
            }
        }

        public void Delete(string entityName, Guid id)
        {
            EnsureWritable(entityName);
            Table(entityName).Remove(id);
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            InMemoryQueryEngine engine = new InMemoryQueryEngine(name => Table(name).Values, _currentUserId);
            switch (query)
            {
                case QueryExpression qe:
                    return engine.ExecuteQueryExpression(qe);
                case QueryByAttribute qba:
                    return engine.ExecuteQueryByAttribute(qba);
                case FetchExpression fetch:
                    return FetchXmlParser.IsAggregate(fetch.Query)
                        ? engine.ExecuteAggregate(FetchXmlParser.ParseAggregate(fetch.Query))
                        : engine.ExecuteQueryExpression(FetchXmlParser.Parse(fetch.Query));
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService supports QueryExpression, QueryByAttribute and FetchExpression. " +
                        $"{query.GetType().Name} is not supported yet.");
            }
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            switch (request)
            {
                case CreateRequest create:
                {
                    CreateResponse response = new CreateResponse();
                    response.Results["id"] = Create(create.Target);
                    return response;
                }

                case UpdateRequest update:
                {
                    Update(update.Target);
                    return new UpdateResponse();
                }

                case DeleteRequest delete:
                {
                    Delete(delete.Target.LogicalName, delete.Target.Id);
                    return new DeleteResponse();
                }

                case RetrieveRequest retrieve:
                {
                    RetrieveResponse response = new RetrieveResponse();
                    response.Results["Entity"] = Retrieve(retrieve.Target.LogicalName, retrieve.Target.Id, retrieve.ColumnSet);
                    return response;
                }

                case RetrieveMultipleRequest retrieveMultiple:
                {
                    RetrieveMultipleResponse response = new RetrieveMultipleResponse();
                    response.Results["EntityCollection"] = RetrieveMultiple(retrieveMultiple.Query);
                    return response;
                }

                case CreateMultipleRequest createMultiple:
                {
                    Guid[] ids = createMultiple.Targets.Entities.Select(Create).ToArray();
                    CreateMultipleResponse response = new CreateMultipleResponse();
                    response.Results["Ids"] = ids;
                    return response;
                }

                case UpdateMultipleRequest updateMultiple:
                {
                    foreach (Entity entity in updateMultiple.Targets.Entities)
                    {
                        Update(entity);
                    }

                    return new UpdateMultipleResponse();
                }

                case AssignRequest assign:
                {
                    // Modern Dataverse models assignment as an ownerid Update, which re-derives the owning columns.
                    Update(new Entity(assign.Target.LogicalName, assign.Target.Id) { ["ownerid"] = assign.Assignee });
                    return new AssignResponse();
                }

                case AssociateRequest associate:
                {
                    Associate(associate.Target.LogicalName, associate.Target.Id, associate.Relationship, associate.RelatedEntities);
                    return new AssociateResponse();
                }

                case DisassociateRequest disassociate:
                {
                    Disassociate(disassociate.Target.LogicalName, disassociate.Target.Id, disassociate.Relationship, disassociate.RelatedEntities);
                    return new DisassociateResponse();
                }

                case RetrieveEntityRequest retrieveEntity:
                {
                    EntityMetadata metadata = ResolveMetadata(retrieveEntity.LogicalName, retrieveEntity.MetadataId);
                    RetrieveEntityResponse response = new RetrieveEntityResponse();
                    response.Results["EntityMetadata"] = metadata;
                    return response;
                }

                case RetrieveAllEntitiesRequest:
                {
                    RetrieveAllEntitiesResponse response = new RetrieveAllEntitiesResponse();
                    response.Results["EntityMetadata"] = _metadata.Values.ToArray();
                    return response;
                }

                case RetrieveAttributeRequest retrieveAttribute:
                {
                    EntityMetadata metadata = ResolveMetadata(retrieveAttribute.EntityLogicalName, Guid.Empty);
                    AttributeMetadata? attribute = metadata.Attributes?
                        .FirstOrDefault(a => string.Equals(a.LogicalName, retrieveAttribute.LogicalName, StringComparison.OrdinalIgnoreCase));
                    if (attribute == null)
                    {
                        throw new InvalidOperationException(
                            $"Attribute metadata '{retrieveAttribute.EntityLogicalName}.{retrieveAttribute.LogicalName}' is not registered. " +
                            $"Add it via FakeEntityMetadata.For(...).Attribute(...) and WithEntityMetadata(...).");
                    }

                    RetrieveAttributeResponse response = new RetrieveAttributeResponse();
                    response.Results["AttributeMetadata"] = attribute;
                    return response;
                }

                case ExecuteMultipleRequest executeMultiple:
                    return ExecuteMultiple(executeMultiple);

                case ExecuteTransactionRequest executeTransaction:
                    return ExecuteTransaction(executeTransaction);

                default:
                    // The File/Image block messages have no strongly-typed request class in the referenced SDK,
                    // so they are matched by RequestName (callers can use a plain OrganizationRequest).
                    return ExecuteByName(request);
            }
        }

        private OrganizationResponse ExecuteByName(OrganizationRequest request)
        {
            switch (request.RequestName)
            {
                case "InitializeFileBlocksUpload":
                    return InitializeFileBlocksUpload(request);
                case "UploadBlock":
                    return UploadBlock(request);
                case "CommitFileBlocksUpload":
                    return CommitFileBlocksUpload(request);
                case "InitializeFileBlocksDownload":
                    return InitializeFileBlocksDownload(request);
                case "DownloadBlock":
                    return DownloadBlock(request);
                case "DeleteFile":
                    return DeleteFile(request);
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService.Execute handles Create/Update/Delete/Retrieve/RetrieveMultiple, " +
                        $"CreateMultiple/UpdateMultiple, Assign/Associate/Disassociate, ExecuteMultiple/ExecuteTransaction, the " +
                        $"File/Image block messages (InitializeFileBlocksUpload/UploadBlock/CommitFileBlocksUpload/" +
                        $"InitializeFileBlocksDownload/DownloadBlock/DeleteFile), and the metadata reads " +
                        $"RetrieveEntity/RetrieveAllEntities/RetrieveAttribute. For other messages, inject a service that " +
                        $"handles '{request.RequestName}' via WithOrganizationService.");
            }
        }

        // Runs each child request in order. ContinueOnError keeps going past a fault; ReturnResponses collects
        // per-item responses. Mirrors the platform's ExecuteMultiple (no transaction — see ExecuteTransaction).
        private ExecuteMultipleResponse ExecuteMultiple(ExecuteMultipleRequest request)
        {
            bool continueOnError = request.Settings?.ContinueOnError ?? false;
            bool returnResponses = request.Settings?.ReturnResponses ?? false;
            ExecuteMultipleResponseItemCollection items = new ExecuteMultipleResponseItemCollection();
            bool faulted = false;

            for (int i = 0; i < request.Requests.Count; i++)
            {
                try
                {
                    OrganizationResponse childResponse = Execute(request.Requests[i]);
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
                        Fault = new OrganizationServiceFault { Message = ex.Message },
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

        // All-or-nothing: snapshot the store, run every child request, and roll back the whole batch if any throws.
        private ExecuteTransactionResponse ExecuteTransaction(ExecuteTransactionRequest request)
        {
            Dictionary<string, Dictionary<Guid, Entity>> snapshot = Snapshot();
            OrganizationResponseCollection responses = new OrganizationResponseCollection();

            try
            {
                foreach (OrganizationRequest child in request.Requests)
                {
                    OrganizationResponse childResponse = Execute(child);
                    if (request.ReturnResponses == true)
                    {
                        responses.Add(childResponse);
                    }
                }
            }
            catch
            {
                RestoreTo(snapshot);
                throw;
            }

            ExecuteTransactionResponse response = new ExecuteTransactionResponse();
            response.Results["Responses"] = responses;
            return response;
        }

        private EntityMetadata ResolveMetadata(string? logicalName, Guid metadataId)
        {
            if (!string.IsNullOrWhiteSpace(logicalName) && _metadata.TryGetValue(logicalName!, out EntityMetadata? byName))
            {
                return byName;
            }

            if (metadataId != Guid.Empty)
            {
                EntityMetadata? byId = _metadata.Values.FirstOrDefault(m => m.MetadataId == metadataId);
                if (byId != null)
                {
                    return byId;
                }
            }

            throw new InvalidOperationException(
                $"Entity metadata '{logicalName}' is not registered. Register it with " +
                $"WithEntityMetadata(FakeEntityMetadata.For(\"{logicalName}\")...Build()).");
        }

        /// <summary>
        /// Associates records over a many-to-many relationship by creating its intersect rows. The relationship must be
        /// declared with <see cref="WithManyToMany"/>. A 1:N "association" is just an <c>Update</c> of the lookup column.
        /// Idempotent: an existing pair is not duplicated.
        /// </summary>
        // Resolves an N:N relationship from an explicit WithManyToMany(...) declaration, falling back to a
        // ManyToManyRelationshipMetadata carried by a registered EntityMetadata (so WithManyToMany is optional
        // once metadata is registered). Throws the registry's guidance message if neither has it.
        private RelationshipRegistry.ManyToMany ResolveManyToMany(string schemaName)
        {
            if (_relationships.TryResolve(schemaName, out RelationshipRegistry.ManyToMany declared))
            {
                return declared;
            }

            foreach (EntityMetadata meta in _metadata.Values)
            {
                ManyToManyRelationshipMetadata? m2m = meta.ManyToManyRelationships?
                    .FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
                if (m2m != null)
                {
                    return new RelationshipRegistry.ManyToMany(
                        m2m.IntersectEntityName,
                        m2m.Entity1LogicalName,
                        m2m.Entity1IntersectAttribute,
                        m2m.Entity2LogicalName,
                        m2m.Entity2IntersectAttribute);
                }
            }

            return _relationships.Resolve(schemaName); // not found anywhere → throws the standard guidance
        }

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            RelationshipRegistry.ManyToMany rel = ResolveManyToMany(relationship.SchemaName);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName);
            Dictionary<Guid, Entity> intersect = Table(rel.IntersectEntity);
            EntityReference caller = new EntityReference(entityName, entityId);

            foreach (EntityReference related in relatedEntities)
            {
                bool exists = intersect.Values.Any(row =>
                    EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), entityId) &&
                    EntityValues.AreEqual(EntityValues.GetValue(row, relatedAttribute), related.Id));
                if (exists)
                {
                    continue;
                }

                Entity row = new Entity(rel.IntersectEntity, Guid.NewGuid())
                {
                    [callerAttribute] = caller,
                    [relatedAttribute] = related,
                };
                intersect[row.Id] = row;
            }
        }

        /// <summary>
        /// Disassociates records over a many-to-many relationship by deleting its matching intersect rows. The
        /// relationship must be declared with <see cref="WithManyToMany"/>.
        /// </summary>
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            RelationshipRegistry.ManyToMany rel = ResolveManyToMany(relationship.SchemaName);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName);
            Dictionary<Guid, Entity> intersect = Table(rel.IntersectEntity);
            HashSet<Guid> relatedIds = new HashSet<Guid>(relatedEntities.Select(r => r.Id));

            List<Guid> toRemove = intersect.Values
                .Where(row =>
                    EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), entityId) &&
                    relatedIds.Contains((Guid)(EntityValues.Normalize(EntityValues.GetValue(row, relatedAttribute)) ?? Guid.Empty)))
                .Select(row => row.Id)
                .ToList();

            foreach (Guid id in toRemove)
            {
                intersect.Remove(id);
            }
        }

        // --- Internal helpers used by the pipeline simulator (same assembly) ---

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

        internal Dictionary<string, Dictionary<Guid, Entity>> Snapshot()
        {
            Dictionary<string, Dictionary<Guid, Entity>> copy =
                new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in _store)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                copy[table.Key] = inner;
            }

            return copy;
        }

        internal void RestoreTo(Dictionary<string, Dictionary<Guid, Entity>> snapshot)
        {
            _store.Clear();
            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in snapshot)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                _store[table.Key] = inner;
            }
        }

        // --- File/Image column block protocol (matched by RequestName; see ExecuteByName) ---

        private OrganizationResponse InitializeFileBlocksUpload(OrganizationRequest request)
        {
            EntityReference target = GetParam<EntityReference>(request, "Target");
            string attribute = GetParam<string>(request, "FileAttributeName");
            string fileName = TryGetParam<string>(request, "FileName") ?? string.Empty;

            string token = "vela-upload-" + (++_tokenSequence).ToString();
            _uploads[token] = new UploadSession
            {
                EntityLogicalName = target.LogicalName,
                RecordId = target.Id,
                AttributeName = attribute,
                FileName = fileName
            };

            OrganizationResponse response = new OrganizationResponse { ResponseName = "InitializeFileBlocksUpload" };
            response.Results["FileContinuationToken"] = token;
            return response;
        }

        private OrganizationResponse UploadBlock(OrganizationRequest request)
        {
            UploadSession session = ResolveUpload(GetParam<string>(request, "FileContinuationToken"));
            string blockId = GetParam<string>(request, "BlockId");
            byte[] data = GetParam<byte[]>(request, "BlockData");
            session.Blocks[blockId] = (byte[])data.Clone();
            return new OrganizationResponse { ResponseName = "UploadBlock" };
        }

        private OrganizationResponse CommitFileBlocksUpload(OrganizationRequest request)
        {
            string token = GetParam<string>(request, "FileContinuationToken");
            UploadSession session = ResolveUpload(token);

            // Assemble the blocks in the order the caller lists them (falling back to upload order).
            IEnumerable<string> blockOrder = TryGetParam<object>(request, "BlockList") switch
            {
                IEnumerable<string> ids => ids,
                _ => session.Blocks.Keys
            };

            List<byte> content = new List<byte>();
            foreach (string id in blockOrder)
            {
                if (session.Blocks.TryGetValue(id, out byte[]? block))
                {
                    content.AddRange(block);
                }
            }

            byte[] bytes = content.ToArray();
            Guid fileId = _idSequence();
            _files[fileId] = new FileBlob
            {
                Content = bytes,
                FileName = TryGetParam<string>(request, "FileName") ?? session.FileName,
                MimeType = TryGetParam<string>(request, "MimeType") ?? "application/octet-stream"
            };

            // The File column stores its fileid; the content is fetched via the download messages.
            Update(new Entity(session.EntityLogicalName, session.RecordId) { [session.AttributeName] = fileId });
            _uploads.Remove(token);

            OrganizationResponse response = new OrganizationResponse { ResponseName = "CommitFileBlocksUpload" };
            response.Results["FileId"] = fileId;
            response.Results["FileSizeInBytes"] = (long)bytes.Length;
            return response;
        }

        private OrganizationResponse InitializeFileBlocksDownload(OrganizationRequest request)
        {
            EntityReference target = GetParam<EntityReference>(request, "Target");
            string attribute = GetParam<string>(request, "FileAttributeName");

            if (!Table(target.LogicalName).TryGetValue(target.Id, out Entity? record) || !record.Contains(attribute) || record[attribute] == null)
            {
                throw new InvalidOperationException(
                    $"No file/image is set on '{target.LogicalName}.{attribute}' for record {target.Id}.");
            }

            (byte[] content, string fileName) = ResolveFileContent(record[attribute], $"{target.LogicalName}.{attribute}");
            string token = "vela-download-" + (++_tokenSequence).ToString();
            _downloads[token] = content;

            OrganizationResponse response = new OrganizationResponse { ResponseName = "InitializeFileBlocksDownload" };
            response.Results["FileContinuationToken"] = token;
            response.Results["FileName"] = fileName;
            response.Results["FileSizeInBytes"] = (long)content.Length;
            response.Results["IsChunkingSupported"] = true;
            return response;
        }

        private OrganizationResponse DownloadBlock(OrganizationRequest request)
        {
            string token = GetParam<string>(request, "FileContinuationToken");
            if (!_downloads.TryGetValue(token, out byte[]? content))
            {
                throw new InvalidOperationException($"Unknown file continuation token '{token}'. Call InitializeFileBlocksDownload first.");
            }

            long offset = Convert.ToInt64(TryGetParam<object>(request, "Offset") ?? 0L);
            long requested = Convert.ToInt64(TryGetParam<object>(request, "BlockLength") ?? (long)content.Length);
            int start = (int)Math.Min(Math.Max(offset, 0L), content.Length);
            int count = (int)Math.Min(Math.Max(requested, 0L), content.Length - start);
            byte[] slice = new byte[count];
            Array.Copy(content, start, slice, 0, count);

            OrganizationResponse response = new OrganizationResponse { ResponseName = "DownloadBlock" };
            response.Results["Data"] = slice;
            return response;
        }

        private OrganizationResponse DeleteFile(OrganizationRequest request)
        {
            Guid fileId = GetParam<Guid>(request, "FileId");
            _files.Remove(fileId);

            // Clear any File column that referenced the deleted file, like the platform.
            foreach (Dictionary<Guid, Entity> table in _store.Values)
            {
                foreach (Entity record in table.Values)
                {
                    foreach (string attribute in record.Attributes.Keys.ToArray())
                    {
                        if (record[attribute] is Guid value && value == fileId)
                        {
                            record.Attributes.Remove(attribute);
                        }
                    }
                }
            }

            return new OrganizationResponse { ResponseName = "DeleteFile" };
        }

        // File column -> its content lives in the blob store keyed by fileid. Image column (or any column that
        // holds bytes inline) -> the stored bytes ARE the content/thumbnail.
        private (byte[] Content, string FileName) ResolveFileContent(object value, string column)
        {
            switch (value)
            {
                case Guid fileId when _files.TryGetValue(fileId, out FileBlob? blob):
                    return (blob.Content, blob.FileName);
                case byte[] bytes:
                    return (bytes, string.Empty);
                default:
                    throw new InvalidOperationException(
                        $"'{column}' does not hold a known fileid or inline bytes; upload a file first or set the column to a byte[].");
            }
        }

        private UploadSession ResolveUpload(string token)
        {
            if (!_uploads.TryGetValue(token, out UploadSession? session))
            {
                throw new InvalidOperationException($"Unknown file continuation token '{token}'. Call InitializeFileBlocksUpload first.");
            }

            return session;
        }

        private static T GetParam<T>(OrganizationRequest request, string name)
        {
            if (!request.Parameters.TryGetValue(name, out object? value) || value is not T typed)
            {
                throw new InvalidOperationException($"Request '{request.RequestName}' requires a '{name}' parameter of type {typeof(T).Name}.");
            }

            return typed;
        }

        private static T? TryGetParam<T>(OrganizationRequest request, string name)
            where T : class
            => request.Parameters.TryGetValue(name, out object? value) && value is T typed ? typed : null;

        private sealed class FileBlob
        {
            public byte[] Content = Array.Empty<byte>();
            public string FileName = string.Empty;
            public string MimeType = string.Empty;
        }

        private sealed class UploadSession
        {
            public string EntityLogicalName = string.Empty;
            public Guid RecordId;
            public string AttributeName = string.Empty;
            public string FileName = string.Empty;
            public readonly Dictionary<string, byte[]> Blocks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        private Dictionary<Guid, Entity> Table(string logicalName)
        {
            if (!_store.TryGetValue(logicalName, out Dictionary<Guid, Entity>? table))
            {
                table = new Dictionary<Guid, Entity>();
                _store[logicalName] = table;
            }

            return table;
        }

        private static Entity Project(Entity entity, ColumnSet columnSet)
        {
            if (columnSet == null || columnSet.AllColumns)
            {
                return EntityValues.Clone(entity);
            }

            Entity projected = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (string column in columnSet.Columns)
            {
                if (entity.Contains(column))
                {
                    projected[column] = entity[column];
                }
            }

            return projected;
        }
    }
}
