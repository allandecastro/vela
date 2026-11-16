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
    public sealed partial class InMemoryOrganizationService : IOrganizationService
    {
        private readonly Dictionary<string, Dictionary<Guid, Entity>> _store =
            new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);

        // Platform behaviours are metadata-driven, so they are opt-in: a test declares them explicitly.
        private readonly AlternateKeyValidator _alternateKeys = new AlternateKeyValidator();

        private readonly AutoNumberApplier _autoNumbers = new AutoNumberApplier();
        private readonly Dictionary<string, int> _defaultStatuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly SystemFieldApplier _systemFields = new SystemFieldApplier();

        // Virtual tables declared read-only: Create/Update/Delete are rejected, like a read-only external provider.
        private readonly HashSet<string> _readOnlyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly RelationshipRegistry _relationships = new RelationshipRegistry();

        private readonly OneToManyRegistry _oneToMany = new OneToManyRegistry();

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

        // Opt-in security model. Null means no enforcement: Create/Retrieve/Update/Delete and RetrieveMultiple
        // behave exactly as before (full access). Set via WithSecurity.
        private SecurityModel? _security;

        // Strict-security guard (opt-in via WithStrictSecurity). When set, a security-relevant operation run without a
        // configured security model fails loudly instead of silently granting full access - so an access-control test
        // that forgot WithSecurity(...) cannot pass green. No effect once WithSecurity is configured.
        private bool _strictSecurity;

        // Record-level shares (PoA). Always present so Grant/RetrievePrincipalAccess work with or without a
        // security model; only consulted for enforcement when _security is set.
        private readonly SharingTable _sharing = new SharingTable();

        // Rows stored by Seed (id -> table) whose inherited access is still derived from the setup: re-derived whenever
        // WithSecurity, a relationship or a WithSharedRecord is declared, so setup order does not matter. A run-time
        // Share/Unshare cascade that reaches a row removes it, so a later setup call does not overwrite that grant.
        private Dictionary<Guid, string> _seededRows = new Dictionary<Guid, string>();

        // Access-team grants (AddUserToRecordTeam) and the registered team templates (template id -> rights).
        private readonly AccessTeamGrants _accessTeams = new AccessTeamGrants();
        private readonly Dictionary<Guid, AccessRights> _accessTeamTemplates = new Dictionary<Guid, AccessRights>();

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
        /// Enables opt-in security enforcement. Configure business units, named roles, users, teams and team
        /// membership; the service then enforces table privileges on Create/Retrieve/Update/Delete (throwing
        /// <c>FaultException&lt;OrganizationServiceFault&gt;</c> with the PrincipalPrivilegeDenied code when the
        /// current user lacks a privilege) and filters RetrieveMultiple to records the user may read. The current
        /// user is the one set by <see cref="WithCurrentUser"/>; its business unit (for depth checks and
        /// <c>owningbusinessunit</c> stamping) comes from its <c>User(...)</c> registration. Without this call there
        /// is no enforcement — full access, as before.
        /// </summary>
        public InMemoryOrganizationService WithSecurity(Action<SecurityConfiguration> configure)
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            SecurityConfiguration configuration = new SecurityConfiguration();
            configure(configuration);
            _security = configuration.Build(_sharing, _accessTeams);
            MaterializeSecurityTables();
            DeriveSeededAccess();
            return this;
        }

        /// <summary>
        /// Opts into strict security: a security-relevant operation (Create/Retrieve/Update/Delete/Associate/… and
        /// RetrieveMultiple) run without a configured security model throws instead of silently granting full access.
        /// This closes the default-off footgun where an access-control test that forgot <see cref="WithSecurity"/>
        /// passes green because everything is allowed. Call it alongside <see cref="WithSecurity"/> to assert that
        /// enforcement was intended; on its own it makes a missing security model fail loudly on the first operation.
        /// </summary>
        public InMemoryOrganizationService WithStrictSecurity()
        {
            _strictSecurity = true;
            return this;
        }

        /// <summary>
        /// Registers an access-team template and the <paramref name="rights"/> it grants. A plugin then adds a user
        /// to a record's access team with <c>AddUserToRecordTeamRequest</c> (and removes them with
        /// <c>RemoveUserFromRecordTeamRequest</c>), giving that user the template's rights on that record — unioned
        /// with role and shared access. Only enforced once <see cref="WithSecurity"/> is set.
        /// </summary>
        public InMemoryOrganizationService WithAccessTeamTemplate(Guid templateId, AccessRights rights)
        {
            _accessTeamTemplates[templateId] = rights;
            return this;
        }


        /// <summary>
        /// Pre-shares a record with a principal (arrange helper, like <see cref="Seed"/>): grants the principal
        /// (a user or team) the given <paramref name="rights"/> on the target, without going through the
        /// GrantAccess message or its Share-privilege check. Only takes effect once <see cref="WithSecurity"/> is set,
        /// and only for the privileges the principal holds on the table. A child seeded under the record inherits the
        /// share through the Reparent cascade, whether it is seeded before or after this call, and so does a child
        /// created under it afterwards; a child already created under it gets nothing.
        /// </summary>
        public InMemoryOrganizationService WithSharedRecord(EntityReference target, EntityReference principal, AccessRights rights)
        {
            _sharing.Grant(target, new PrincipalAccess { Principal = principal, AccessMask = rights });
            DeriveSeededAccess();
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

        // The current time on the store's clock (WithClock), which the pipeline's plug-in cache expires by too.
        internal DateTime Now() => _clock();

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
        /// (<c>createdon</c>/<c>createdby</c>/<c>modifiedon</c>/<c>modifiedby</c>) but no owner columns. A declaration
        /// wins over the <c>OwnershipType</c> of registered metadata and over the early-bound type.
        /// </summary>
        public InMemoryOrganizationService AsOrganizationOwned(string entityLogicalName)
        {
            _systemFields.Declare(entityLogicalName, OwnershipKind.OrganizationOwned);
            return this;
        }

        /// <summary>
        /// Declares a table as user/team-owned (the default): Create/Update stamp audit columns plus
        /// <c>ownerid</c>, <c>owninguser</c>/<c>owningteam</c> and <c>owningbusinessunit</c>. Use this to override
        /// what registered metadata (<see cref="WithEntityMetadata(EntityMetadata[])"/>) or the early-bound type would
        /// infer. With none of the three, a table is user-owned.
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
            if (!string.IsNullOrWhiteSpace(relationshipSchemaName)
                && _oneToMany.TryResolveBySchema(relationshipSchemaName, out OneToManyRegistry.OneToMany oneToMany)
                && oneToMany.HasSchemaName)
            {
                throw DuplicateSchemaName(relationshipSchemaName);
            }

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
        /// Declares a one-to-many (1:N) relationship and its cascade behaviour, so Delete (and later Assign/Share)
        /// cascade from the parent (<paramref name="referencedEntity"/>) to the children
        /// (<paramref name="referencingEntity"/> rows whose <paramref name="referencingAttribute"/> lookup points at
        /// the parent). Use <see cref="WithParentalRelationship"/>/<see cref="WithReferentialRelationship"/> for the
        /// common presets. Like Dataverse, a table can be the child of only one <b>parental</b> relationship (one
        /// where Assign/Reparent/Share/Unshare cascades or Delete is Cascade); declaring a second one throws
        /// <see cref="InvalidOperationException"/>, unless it is over the same lookup column (a polymorphic lookup).
        /// <para>
        /// <paramref name="schemaName"/> is the relationship's real schema name (for example
        /// <c>contact_customer_accounts</c>): <c>AssociateRequest</c>/<c>DisassociateRequest</c>, a deep insert and a
        /// <c>RelatedEntitiesQuery</c> find the relationship by it. Two relationships cannot share a schema name. Without
        /// it the relationship is named <c>{referencedEntity}_{referencingEntity}</c>.
        /// </para>
        /// </summary>
        public InMemoryOrganizationService WithRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, CascadeConfiguration cascade, string? schemaName = null)
        {
            if (string.IsNullOrWhiteSpace(referencedEntity) || string.IsNullOrWhiteSpace(referencingEntity) || string.IsNullOrWhiteSpace(referencingAttribute))
            {
                throw new ArgumentException("Referenced/referencing entity and the referencing attribute are required.");
            }

            bool named = !string.IsNullOrWhiteSpace(schemaName);
            if (named && _relationships.TryResolve(schemaName!, out _))
            {
                throw DuplicateSchemaName(schemaName!);
            }

            _oneToMany.Add(new OneToManyRegistry.OneToMany(
                named ? schemaName! : $"{referencedEntity}_{referencingEntity}", referencedEntity, referencingEntity, referencingAttribute, cascade, named));
            DeriveSeededAccess();
            return this;
        }

        internal static InvalidOperationException DuplicateSchemaName(string schemaName) =>
            new InvalidOperationException(
                $"A relationship with schema name '{schemaName}' is already declared on the in-memory service. Relationship schema names are unique.");

        /// <summary>
        /// Declares a <b>parental</b> 1:N relationship: every action (incl. Delete) cascades to children.
        /// <paramref name="schemaName"/> is the relationship's real schema name (see
        /// <see cref="WithRelationship(string, string, string, CascadeConfiguration, string)"/>).
        /// </summary>
        public InMemoryOrganizationService WithParentalRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, string? schemaName = null) =>
            WithRelationship(referencedEntity, referencingEntity, referencingAttribute, new CascadeConfiguration
            {
                Assign = CascadeType.Cascade,
                Delete = CascadeType.Cascade,
                Merge = CascadeType.Cascade,
                Reparent = CascadeType.Cascade,
                Share = CascadeType.Cascade,
                Unshare = CascadeType.Cascade,
            }, schemaName);

        /// <summary>
        /// Declares a <b>referential</b> 1:N relationship: Delete removes the child's link and Merge moves the child to
        /// the merged-into record; nothing else cascades. Set <paramref name="restrictDelete"/> to block deleting the parent while children exist.
        /// <paramref name="schemaName"/> is the relationship's real schema name (see
        /// <see cref="WithRelationship(string, string, string, CascadeConfiguration, string)"/>).
        /// </summary>
        public InMemoryOrganizationService WithReferentialRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, bool restrictDelete = false, string? schemaName = null) =>
            WithRelationship(referencedEntity, referencingEntity, referencingAttribute, new CascadeConfiguration
            {
                Assign = CascadeType.NoCascade,
                Delete = restrictDelete ? CascadeType.Restrict : CascadeType.RemoveLink,
                // Like the platform's referential relationships (contact_customer_accounts, account_parent_account):
                // Merge reparents the children to the master.
                Merge = CascadeType.Cascade,
                Reparent = CascadeType.NoCascade,
                Share = CascadeType.NoCascade,
                Unshare = CascadeType.NoCascade,
            }, schemaName);

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
                _systemFields.Register(logicalName!, entity!.OwnershipType);
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

        /// <summary>
        /// Declares the default status reason of a state: the <c>statuscode</c> a record of the table gets when a
        /// write sets <c>statecode</c> without <c>statuscode</c> (or a SetState passes status -1). Undeclared states use
        /// the standard default: state 0 (active) gets status 1, any other state status 2.
        /// </summary>
        public InMemoryOrganizationService WithDefaultStatus(string entityLogicalName, int state, int status)
        {
            if (string.IsNullOrEmpty(entityLogicalName))
            {
                throw new ArgumentException("A table logical name is required.", nameof(entityLogicalName));
            }

            _defaultStatuses[DefaultStatusKey(entityLogicalName, state)] = status;
            return this;
        }

        // The status a state change with no explicit status lands on (see WithDefaultStatus).
        internal int DefaultStatus(string entityLogicalName, int state)
        {
            return _defaultStatuses.TryGetValue(DefaultStatusKey(entityLogicalName, state), out int status)
                ? status
                : (state == 0 ? 1 : 2);
        }

        private static string DefaultStatusKey(string entityLogicalName, int state) => entityLogicalName + "|" + state;

        /// <summary>
        /// Pre-populates the store without going through Create (ids are assigned when empty). With a security model
        /// configured, a seeded child of a parental relationship gets the inherited access a Create under its parent
        /// would give it. The setup may come in any order: the seeded rows are re-derived when
        /// <see cref="WithSecurity"/>, a relationship or a <see cref="WithSharedRecord"/> is declared after them.
        /// </summary>
        public InMemoryOrganizationService Seed(params Entity[] entities)
        {
            foreach (Entity entity in entities)
            {
                Entity stored = EntityValues.Clone(entity);
                if (stored.Id == Guid.Empty)
                {
                    stored.Id = _idSequence();
                }

                // A seeded role copy is owned by its business unit, like the materialized ones.
                if (string.Equals(stored.LogicalName, RoleTable, StringComparison.OrdinalIgnoreCase))
                {
                    StampRoleBusinessUnit(stored);
                }

                Table(stored.LogicalName)[stored.Id] = stored;
                _seededRows[stored.Id] = stored.LogicalName;
            }

            // Derived once every row of the call is stored, so a parent may be listed after its child.
            DeriveSeededAccess();
            return this;
        }

        /// <summary>Returns a snapshot (clones) of all stored records of the given logical name.</summary>
        public IReadOnlyList<Entity> GetAll(string logicalName)
        {
            return Table(logicalName).Values.Select(EntityValues.Clone).ToList();
        }

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

        // 0x80040217 ObjectDoesNotExist — the platform's fault code for a record that does not exist. Real plugins
        // (and Vela's own RetryPolicy) branch on this exact ErrorCode, and the harness's security layer already
        // surfaces a FaultException for privilege denials, so a not-found must be a FaultException too (not a bare
        // InvalidOperationException) for the double to stay substitutable with a real IOrganizationService.
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);

        private static FaultException<OrganizationServiceFault> RecordDoesNotExist(string entityName, Guid id)
        {
            string message = $"{entityName} With Id = {id:D} Does Not Exist";
            return new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = ObjectDoesNotExist, Message = message },
                new FaultReason(message));
        }

        // 0x80040237 DuplicateRecord — the platform's fault for inserting a row whose primary key already exists.
        private const int DuplicateRecord = unchecked((int)0x80040237);

        private Entity StoredOrThrow(EntityReference record)
            => StoredOrNull(record.LogicalName, record.Id) ?? throw RecordDoesNotExist(record.LogicalName, record.Id);

        private static FaultException<OrganizationServiceFault> RecordAlreadyExists(string entityName, Guid id)
        {
            string message = $"A {entityName} record with Id = {id:D} already exists.";
            return new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = DuplicateRecord, Message = message },
                new FaultReason(message));
        }

        private static FaultException<OrganizationServiceFault> AssociationAlreadyExists(string entityName, Guid entityId, EntityReference related)
        {
            string message = $"Cannot associate {entityName} {entityId:D} with {related.LogicalName} {related.Id:D}: the records are already associated.";
            return new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = DuplicateRecord, Message = message },
                new FaultReason(message));
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

        // Include support: fills a retrieved record's RelatedEntities from a RelationshipQueryCollection (1:N
        // children and N:1 parent). The relationship is resolved against the declared 1:N relationships by schema
        // name (the one given to WithRelationship), else by the (this-entity, other-entity) pair, mirroring how the
        // deep insert resolves relationships. Declare relationships with WithRelationship for this to resolve.
        private void PopulateRetrievedRelations(Entity entity, string entityName, Guid id, RelationshipQueryCollection related)
        {
            if (!Table(entityName).TryGetValue(id, out Entity? stored))
            {
                return;
            }

            foreach (KeyValuePair<Relationship, QueryBase> pair in related)
            {
                if (pair.Value is not QueryExpression childQuery || string.IsNullOrEmpty(childQuery.EntityName))
                {
                    continue;
                }

                string otherEntity = childQuery.EntityName;
                ColumnSet columns = childQuery.ColumnSet ?? new ColumnSet(true);

                // N:N: resolved by the real schema name (the registry / metadata key it directly). Walk the intersect
                // rows on this entity's side and retrieve the related records.
                if (TryResolveManyToMany(pair.Key.SchemaName, out RelationshipRegistry.ManyToMany m2m))
                {
                    (string callerAttribute, string relatedAttribute) = m2m.AttributesFor(entityName);
                    List<object> associatedIds = new List<object>();
                    foreach (Entity row in Table(m2m.IntersectEntity).Values)
                    {
                        if (EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), id) &&
                            EntityValues.GetValue(row, relatedAttribute) is EntityReference relatedRef &&
                            Table(otherEntity).ContainsKey(relatedRef.Id))
                        {
                            associatedIds.Add(relatedRef.Id);
                        }
                    }

                    // Materialize the associated records through the same query path as RetrieveMultiple so the
                    // child query's Criteria/Orders/TopCount/paging are honoured (an empty set yields no rows).
                    QueryExpression associatedQuery = new QueryExpression(otherEntity) { ColumnSet = columns };
                    associatedQuery.Criteria.AddCondition(otherEntity + "id", ConditionOperator.In, associatedIds.ToArray());
                    ApplyChildQueryShape(associatedQuery, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(associatedQuery);
                    continue;
                }

                // 1:N: this entity is the parent (referenced) side -> children whose lookup points back at it.
                // Prefer an exact schema-name match (when two relationships link the same table pair), else the pair.
                List<OneToManyRegistry.OneToMany> childCandidates = _oneToMany.ChildRelationshipsOf(entityName)
                    .Where(r => string.Equals(r.ReferencingEntity, otherEntity, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                OneToManyRegistry.OneToMany? oneToMany =
                    childCandidates.FirstOrDefault(r => string.Equals(r.SchemaName, pair.Key.SchemaName, StringComparison.OrdinalIgnoreCase))
                    ?? childCandidates.FirstOrDefault();
                if (oneToMany != null)
                {
                    QueryExpression query = new QueryExpression(otherEntity) { ColumnSet = columns };
                    query.Criteria.AddCondition(oneToMany.ReferencingAttribute, ConditionOperator.Equal, id);
                    ApplyChildQueryShape(query, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(query);
                    continue;
                }

                // N:1: this entity is the child (referencing) side -> the single parent its lookup points at.
                // Prefer an exact schema-name match, else the table pair.
                List<OneToManyRegistry.OneToMany> parentCandidates = _oneToMany.ParentRelationshipsOf(entityName)
                    .Where(r => string.Equals(r.ReferencedEntity, otherEntity, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                OneToManyRegistry.OneToMany? manyToOne =
                    parentCandidates.FirstOrDefault(r => string.Equals(r.SchemaName, pair.Key.SchemaName, StringComparison.OrdinalIgnoreCase))
                    ?? parentCandidates.FirstOrDefault();
                if (manyToOne != null &&
                    stored.GetAttributeValue<EntityReference>(manyToOne.ReferencingAttribute) is EntityReference parentRef &&
                    Table(otherEntity).ContainsKey(parentRef.Id))
                {
                    // Run through the query path so the child query's Criteria/Orders/paging apply to the parent too
                    // (e.g. a Criteria that the parent fails to match filters it out, as it does on a real org).
                    QueryExpression parentQuery = new QueryExpression(otherEntity) { ColumnSet = columns };
                    parentQuery.Criteria.AddCondition(otherEntity + "id", ConditionOperator.Equal, parentRef.Id);
                    ApplyChildQueryShape(parentQuery, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(parentQuery);
                }
            }
        }

        // Copies a related-entities child query's shape - its Criteria (nested under the fake's own relationship
        // filter so the caller's query object is never mutated), Orders, TopCount, paging and Distinct - onto the
        // query the fake builds to materialize the relation, so Include honours the child query like a real org.
        private static void ApplyChildQueryShape(QueryExpression built, QueryExpression childQuery)
        {
            if (childQuery.Criteria != null &&
                (childQuery.Criteria.Conditions.Count > 0 || childQuery.Criteria.Filters.Count > 0))
            {
                built.Criteria.AddFilter(childQuery.Criteria);
            }

            built.Orders.AddRange(childQuery.Orders);
            built.TopCount = childQuery.TopCount;
            built.PageInfo = childQuery.PageInfo;
            built.Distinct = childQuery.Distinct;
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

        // Assign is not a message of an organization-owned table (verified live).
        internal void DemandAssignable(string entityLogicalName)
        {
            if (_systemFields.IsKnownOrganizationOwned(entityLogicalName))
            {
                throw PlatformFault.Create(PlatformFault.MessageNotSupportedForEntity,
                    $"The 'Assign' method does not support entities of type '{entityLogicalName}'.");
            }
        }

        internal Entity SetStateUpdate(EntityReference record, OptionSetValue? state, OptionSetValue? status)
        {
            int stateValue = state?.Value ?? 0;
            int statusValue = status != null && status.Value >= 0 ? status.Value : DefaultStatus(record.LogicalName, stateValue);
            return new Entity(record.LogicalName, ResolveReference(record))
            {
                [SystemFieldApplier.StateCode] = new OptionSetValue(stateValue),
                [SystemFieldApplier.StatusCode] = new OptionSetValue(statusValue),
            };
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


        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            // Strict security: a read run with no configured security model fails loudly instead of returning
            // everything, so an access-control read test that forgot WithSecurity(...) cannot pass green.
            DemandStrictSecurityConfigured();

            // FetchXML is parsed into the equivalent QueryExpression (aggregates included) and runs through the same
            // checks and engine path, so the two forms of a query can never diverge.
            FetchQuery? parsedFetch = query is FetchExpression fetchExpression ? FetchXmlParser.Parse(fetchExpression.Query) : null;

            // No Read privilege on a table the query reads (root or any link-entity) fails the whole query.
            switch (query)
            {
                case QueryExpression qe:
                    DemandQueryReadPrivileges(qe);
                    break;
                case QueryByAttribute qba:
                    DemandQueryReadPrivileges(new QueryExpression(qba.EntityName));
                    break;
                case FetchExpression:
                    DemandQueryReadPrivileges(parsedFetch!.Query);
                    break;
            }

            // Field-level security: an aggregate/group-by over a field-secured column the caller can't read would
            // otherwise leak it through the aggregate value, so reject the query up front.
            DemandAggregateColumnReads(query);

            // With security on, the read filter is applied inside the engine to the primary rows BEFORE paging, so
            // pages are drawn from the readable set (not trimmed afterwards).
            InMemoryQueryEngine engine = new InMemoryQueryEngine(name => Table(name).Values, _currentUserId, ReadFilter(), _clock());
            EntityCollection result;
            switch (query)
            {
                case QueryExpression qe:
                    result = engine.ExecuteQueryExpression(qe);
                    break;
                case QueryByAttribute qba:
                    result = engine.ExecuteQueryByAttribute(qba);
                    break;
                case FetchExpression:
                    result = engine.ExecuteFetch(parsedFetch!);
                    break;
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService supports QueryExpression, QueryByAttribute and FetchExpression. " +
                        $"{query.GetType().Name} is not supported yet.");
            }

            // Field-level security masks field-secured columns the caller can't read from each returned record.
            if (Enforcing != null)
            {
                foreach (Entity row in result.Entities)
                {
                    MaskSecuredColumns(row);
                }
            }

            foreach (Entity row in result.Entities)
            {
                ShapeRead(row);
            }

            return result;
        }


        public OrganizationResponse Execute(OrganizationRequest request)
        {
            // Upsert is matched by RequestName so both the SDK UpsertRequest and Vela's typed UpsertRequest<T>
            // (a plain OrganizationRequest with RequestName "Upsert") flow through the same create-or-update path.
            if (string.Equals(request.RequestName, "Upsert", StringComparison.OrdinalIgnoreCase) &&
                request.Parameters.TryGetValue("Target", out object? upsertTarget) && upsertTarget is Entity targetEntity)
            {
                return ExecuteUpsert(targetEntity);
            }

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
                    Delete(delete.Target.LogicalName, ResolveReference(delete.Target));
                    return new DeleteResponse();
                }

                case RetrieveRequest retrieve:
                {
                    RetrieveResponse response = new RetrieveResponse();
                    Guid id = ResolveReference(retrieve.Target);
                    Entity entity = Retrieve(retrieve.Target.LogicalName, id, retrieve.ColumnSet);
                    if (retrieve.RelatedEntitiesQuery != null && retrieve.RelatedEntitiesQuery.Count > 0)
                    {
                        PopulateRetrievedRelations(entity, retrieve.Target.LogicalName, id, retrieve.RelatedEntitiesQuery);
                    }

                    response.Results["Entity"] = entity;
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
                    // The whole batch is one transaction: a fault partway through rolls back the rows already written,
                    // so a bulk create is all-or-nothing (matching Dataverse) instead of leaving partial rows.
                    Guid[] ids = RunAtomically(() => createMultiple.Targets.Entities.Select(Create).ToArray());
                    CreateMultipleResponse response = new CreateMultipleResponse();
                    response.Results["Ids"] = ids;
                    return response;
                }

                case UpdateMultipleRequest updateMultiple:
                {
                    RunAtomically(() =>
                    {
                        foreach (Entity entity in updateMultiple.Targets.Entities)
                        {
                            Update(entity);
                        }

                        return true;
                    });

                    return new UpdateMultipleResponse();
                }

                case AssignRequest assign:
                {
                    DemandAssignable(assign.Target.LogicalName);
                    // Modern Dataverse models assignment as an ownerid Update; UpdateCore infers the Assign
                    // privilege from the ownerid change, so both paths enforce identically.
                    UpdateCore(new Entity(assign.Target.LogicalName, ResolveReference(assign.Target)) { ["ownerid"] = assign.Assignee });
                    return new AssignResponse();
                }

                case SetStateRequest setState:
                {
                    // The platform runs SetState as an Update of statecode/statuscode, with status -1 resolved to the
                    // state's default status.
                    UpdateCore(SetStateUpdate(setState.EntityMoniker, setState.State, setState.Status));
                    return new SetStateResponse();
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

                case MergeRequest merge:
                {
                    Merge(merge, null);
                    return new MergeResponse();
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

                case WhoAmIRequest:
                {
                    SystemActor actor = WhoAmIActor();
                    WhoAmIResponse response = new WhoAmIResponse();
                    response.Results["UserId"] = actor.UserId;
                    response.Results["BusinessUnitId"] = actor.BusinessUnitId;
                    response.Results["OrganizationId"] = actor.OrganizationId;
                    return response;
                }

                // Sharing and record-team requests fault on a record that does not exist (verified live for Grant,
                // Modify and Revoke: 0x80040217), whether or not security is on.
                case GrantAccessRequest grant:
                {
                    DemandAccess(grant.Target.LogicalName, Privilege.Share, StoredOrThrow(grant.Target));
                    PrincipalAccess granted = ClampToSharerRights(grant.Target, grant.PrincipalAccess);
                    _sharing.Grant(grant.Target, granted);
                    CascadeShare(grant.Target.LogicalName, grant.Target.Id, granted, replace: false, new HashSet<Guid>());
                    return new GrantAccessResponse();
                }

                case ModifyAccessRequest modify:
                {
                    DemandAccess(modify.Target.LogicalName, Privilege.Share, StoredOrThrow(modify.Target));
                    PrincipalAccess modified = ClampToSharerRights(modify.Target, modify.PrincipalAccess);
                    _sharing.Modify(modify.Target, modified);
                    CascadeShare(modify.Target.LogicalName, modify.Target.Id, modified, replace: true, new HashSet<Guid>());
                    return new ModifyAccessResponse();
                }

                case RevokeAccessRequest revoke:
                {
                    DemandAccess(revoke.Target.LogicalName, Privilege.Share, StoredOrThrow(revoke.Target));
                    _sharing.Revoke(revoke.Target, revoke.Revokee);
                    CascadeUnshare(revoke.Target.LogicalName, revoke.Target.Id, revoke.Revokee, new HashSet<Guid>());
                    return new RevokeAccessResponse();
                }

                case RetrievePrincipalAccessRequest principalAccess:
                {
                    RetrievePrincipalAccessResponse response = new RetrievePrincipalAccessResponse();
                    response.Results["AccessRights"] = EffectiveAccess(principalAccess.Principal, principalAccess.Target);
                    return response;
                }

                case RetrieveSharedPrincipalsAndAccessRequest sharedPrincipals:
                {
                    RetrieveSharedPrincipalsAndAccessResponse response = new RetrieveSharedPrincipalsAndAccessResponse();
                    response.Results["PrincipalAccesses"] = _sharing.SharesOf(sharedPrincipals.Target);
                    return response;
                }

                case AddUserToRecordTeamRequest addToTeam:
                {
                    if (!_accessTeamTemplates.TryGetValue(addToTeam.TeamTemplateId, out AccessRights rights))
                    {
                        throw new InvalidOperationException(
                            $"Access-team template {addToTeam.TeamTemplateId} is not registered. Declare it with WithAccessTeamTemplate(...).");
                    }

                    // Managing a record's access team needs AppendTo on the record, like relating to it.
                    DemandAccess(addToTeam.Record.LogicalName, Privilege.AppendTo, StoredOrThrow(addToTeam.Record));
                    _accessTeams.Add(addToTeam.Record, addToTeam.SystemUserId, addToTeam.TeamTemplateId, rights);
                    AddUserToRecordTeamResponse addResponse = new AddUserToRecordTeamResponse();
                    addResponse.Results["AccessTeamId"] = DeterministicGuid.From("accessteam", addToTeam.Record.Id, addToTeam.TeamTemplateId);
                    return addResponse;
                }

                case RemoveUserFromRecordTeamRequest removeFromTeam:
                {
                    DemandAccess(removeFromTeam.Record.LogicalName, Privilege.AppendTo, StoredOrThrow(removeFromTeam.Record));
                    _accessTeams.Remove(removeFromTeam.Record, removeFromTeam.SystemUserId, removeFromTeam.TeamTemplateId);
                    return new RemoveUserFromRecordTeamResponse();
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
                    throw new MessageNotModelledException(
                        $"InMemoryOrganizationService.Execute handles Create/Update/Upsert/Delete/Retrieve/RetrieveMultiple, " +
                        $"CreateMultiple/UpdateMultiple, Assign/SetState/Associate/Disassociate/Merge, ExecuteMultiple/ExecuteTransaction, " +
                        $"WhoAmI, the sharing messages (GrantAccess/ModifyAccess/RevokeAccess/RetrievePrincipalAccess/" +
                        $"RetrieveSharedPrincipalsAndAccess), the record-team messages (AddUserToRecordTeam/RemoveUserFromRecordTeam), the " +
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
                        // Carry the platform fault's ErrorCode as well as its Message: real plugins branch on the
                        // ErrorCode of a per-item fault (e.g. ObjectDoesNotExist), so a message-only fault would
                        // hide the code the caller inspects.
                        Fault = ExecuteMultipleItemFault(ex),
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

        // Builds the per-item fault for a faulted child request. A FaultException<OrganizationServiceFault> carries
        // the platform fault (ErrorCode + Message) in its Detail, so that is copied through verbatim - mirroring the
        // real ExecuteMultiple. Any other exception has no ErrorCode, so only its message is surfaced.
        private static OrganizationServiceFault ExecuteMultipleItemFault(Exception exception)
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

        // Runs an operation all-or-nothing: snapshot the store, run it, and roll the whole store back if it throws, so
        // a multi-row/multi-record write (bulk create/update, deep insert) leaves no partial rows on a mid-way fault.
        internal T RunAtomically<T>(Func<T> operation)
        {
            StoreSnapshot snapshot = Snapshot();
            try
            {
                return operation();
            }
            catch
            {
                RestoreTo(snapshot);
                throw;
            }
        }

        // All-or-nothing: snapshot the store, run every child request, and roll back the whole batch if any throws.
        private ExecuteTransactionResponse ExecuteTransaction(ExecuteTransactionRequest request)
        {
            StoreSnapshot snapshot = Snapshot();
            OrganizationResponseCollection responses = new OrganizationResponseCollection();

            int index = 0;
            try
            {
                for (; index < request.Requests.Count; index++)
                {
                    OrganizationResponse childResponse = Execute(request.Requests[index]);
                    if (request.ReturnResponses == true)
                    {
                        responses.Add(childResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                RestoreTo(snapshot);
                FaultException<OrganizationServiceFault>? wrapped = PlatformFault.ExecuteTransactionFailed(ex, index);
                if (wrapped != null)
                {
                    throw wrapped;
                }

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

        // Non-throwing N:N resolution: a declared WithManyToMany relationship, else one recovered from registered
        // EntityMetadata. Used by Include reads to tell a N:N schema name from a 1:N/N:1 one.
        private bool TryResolveManyToMany(string schemaName, out RelationshipRegistry.ManyToMany relationship)
        {
            if (_relationships.TryResolve(schemaName, out relationship))
            {
                return true;
            }

            foreach (EntityMetadata meta in _metadata.Values)
            {
                ManyToManyRelationshipMetadata? m2m = meta.ManyToManyRelationships?
                    .FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
                if (m2m != null)
                {
                    relationship = new RelationshipRegistry.ManyToMany(
                        m2m.IntersectEntityName,
                        m2m.Entity1LogicalName,
                        m2m.Entity1IntersectAttribute,
                        m2m.Entity2LogicalName,
                        m2m.Entity2IntersectAttribute);
                    return true;
                }
            }

            relationship = default;
            return false;
        }

        // Every N:N relationship the store knows about - declared via WithManyToMany and derived from registered
        // EntityMetadata - so a delete can clean up intersect rows on either side. Duplicates (a metadata N:N listed
        // on both entities) are harmless since intersect-row removal is idempotent.
        private IEnumerable<RelationshipRegistry.ManyToMany> AllManyToManyRelationships()
        {
            foreach (RelationshipRegistry.ManyToMany declared in _relationships.All)
            {
                yield return declared;
            }

            foreach (EntityMetadata meta in _metadata.Values)
            {
                if (meta.ManyToManyRelationships == null)
                {
                    continue;
                }

                foreach (ManyToManyRelationshipMetadata m2m in meta.ManyToManyRelationships)
                {
                    yield return new RelationshipRegistry.ManyToMany(
                        m2m.IntersectEntityName,
                        m2m.Entity1LogicalName,
                        m2m.Entity1IntersectAttribute,
                        m2m.Entity2LogicalName,
                        m2m.Entity2IntersectAttribute);
                }
            }
        }

        // Verifies the calling record and every related record exist before an Associate/Disassociate touches the
        // intersect, so relating to a missing record throws the platform not-found fault (ObjectDoesNotExist) exactly
        // as a real org does, rather than silently writing a dangling intersect row.
        private void DemandAssociationTargetsExist(string entityName, Guid entityId, EntityReferenceCollection relatedEntities)
        {
            if (!Table(entityName).ContainsKey(entityId))
            {
                throw RecordDoesNotExist(entityName, entityId);
            }

            foreach (EntityReference related in relatedEntities)
            {
                if (!Table(related.LogicalName).ContainsKey(related.Id))
                {
                    throw RecordDoesNotExist(related.LogicalName, related.Id);
                }
            }
        }

        /// <summary>
        /// Associates records. Over a many-to-many relationship (declared with <see cref="WithManyToMany(string, string, string, string, string, string)"/>
        /// or carried by registered metadata) it creates the intersect rows; <paramref name="entityName"/> and the
        /// related records must be the relationship's two sides. Associating a pair that is already associated faults,
        /// and nothing is written. Over a 1:N relationship declared with
        /// <see cref="WithRelationship(string, string, string, CascadeConfiguration, string)"/> it sets the child's lookup
        /// to the parent (an Update), from either side.
        /// </summary>
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            DemandStrictSecurityConfigured();

            // A 1:N "association" is not an intersect row - it sets the child's lookup to the parent (an Update). Only
            // an N:N relationship uses an intersect table. Try N:N first, then 1:N, matching the real Associate; an
            // undeclared relationship throws the standard guidance (before the target-existence check, as before).
            if (!TryResolveManyToMany(relationship.SchemaName, out RelationshipRegistry.ManyToMany rel))
            {
                if (_oneToMany.TryResolveBySchema(relationship.SchemaName, out OneToManyRegistry.OneToMany oneToMany))
                {
                    DemandRoleForReflexive(relationship, oneToMany.ReferencedEntity, oneToMany.ReferencingEntity);
                    DemandOneToManyAccess(entityName, entityId, oneToMany, relatedEntities);
                    DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
                    RunAtomically<object?>(() => { SetOneToManyLink(entityName, entityId, oneToMany, relationship.PrimaryEntityRole, relatedEntities, set: true); return null; });
                    return;
                }

                throw UnknownRelationship(relationship.SchemaName);
            }

            DemandRoleForReflexive(relationship, rel.FirstEntity, rel.SecondEntity);
            DemandManyToManyAccess(entityName, entityId, relatedEntities);
            DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
            DemandManyToManySides(relationship.SchemaName, rel, entityName, relatedEntities);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName, relationship.PrimaryEntityRole);
            Dictionary<Guid, Entity> intersect = Table(rel.IntersectEntity);
            EntityReference caller = new EntityReference(entityName, entityId);

            // Every pair is checked before any row is written, so a rejected pair (one already associated, or the
            // same record listed twice) leaves no partial association behind.
            HashSet<Guid> pending = new HashSet<Guid>();
            foreach (EntityReference related in relatedEntities)
            {
                bool exists = !pending.Add(related.Id) || intersect.Values.Any(row =>
                    EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), entityId) &&
                    EntityValues.AreEqual(EntityValues.GetValue(row, relatedAttribute), related.Id));
                if (exists)
                {
                    // Real Dataverse rejects re-associating an existing N:N pair (a plugin may catch that fault), so
                    // fault here rather than silently no-op'ing - the latter would let an access/dup test pass green.
                    throw AssociationAlreadyExists(entityName, entityId, related);
                }
            }

            foreach (EntityReference related in relatedEntities)
            {
                Entity row = new Entity(rel.IntersectEntity, _idSequence())
                {
                    [callerAttribute] = caller,
                    [relatedAttribute] = related,
                };
                intersect[row.Id] = row;
            }
        }

        // An N:N Associate links the relationship's two tables: the calling record must be one side and every related
        // record the other (the same table for a self-referential relationship). Anything else would write an intersect
        // row naming a table the relationship does not join. The fault code is not verified live.
        private static void DemandManyToManySides(string schemaName, RelationshipRegistry.ManyToMany relationship, string entityName, EntityReferenceCollection relatedEntities)
        {
            bool isFirst = string.Equals(entityName, relationship.FirstEntity, StringComparison.OrdinalIgnoreCase);
            bool isSecond = string.Equals(entityName, relationship.SecondEntity, StringComparison.OrdinalIgnoreCase);
            if (!isFirst && !isSecond)
            {
                throw PlatformFault.Create(PlatformFault.InvalidArgument,
                    $"Cannot associate a {entityName} record over the relationship '{schemaName}': it links {relationship.FirstEntity} and {relationship.SecondEntity}.");
            }

            string otherSide = isFirst ? relationship.SecondEntity : relationship.FirstEntity;
            foreach (EntityReference related in relatedEntities)
            {
                if (!string.Equals(related.LogicalName, otherSide, StringComparison.OrdinalIgnoreCase))
                {
                    throw PlatformFault.Create(PlatformFault.InvalidArgument,
                        $"Cannot associate {entityName} with a {related.LogicalName} record over the relationship '{schemaName}': it links {relationship.FirstEntity} and {relationship.SecondEntity}.");
                }
            }
        }

        // A relationship that does not exist faults with 0x80040216 (verified live). The store knows only the
        // relationships a test declares, so the message also says how to declare it.
        private static FaultException<OrganizationServiceFault> UnknownRelationship(string schemaName) =>
            PlatformFault.Create(PlatformFault.UnknownRelationship,
                $"The Entity Relationship with SchemaName '{schemaName}' was not found in the MetadataCache. Vela.Testing knows " +
                $"only the relationships a test declares: WithManyToMany(\"{schemaName}\", ...), WithRelationship(..., schemaName: " +
                $"\"{schemaName}\") (or WithParentalRelationship/WithReferentialRelationship), or an EntityMetadata carrying it " +
                $"registered with WithEntityMetadata(...).");

        // A self-referential (reflexive) relationship, N:N or 1:N, needs Relationship.PrimaryEntityRole to tell which
        // side the calling record is on; without it Associate/Disassociate fault (verified live).
        private static void DemandRoleForReflexive(Relationship relationship, string firstEntity, string secondEntity)
        {
            if (relationship.PrimaryEntityRole == null && string.Equals(firstEntity, secondEntity, StringComparison.OrdinalIgnoreCase))
            {
                throw PlatformFault.Create(PlatformFault.InvalidArgument,
                    $"Entity role must be specified for reflexive relationship '{relationship.SchemaName}.'");
            }
        }

        // Sets (Associate) or clears (Disassociate) a 1:N lookup: the child's ReferencingAttribute points at the
        // parent. Works from either side - entityName may be the parent (referenced) side, with relatedEntities the
        // children, or the child (referencing) side, with the single related record the parent. On a self-referential
        // relationship the role says which: Referenced makes the calling record the parent (verified live).
        private void SetOneToManyLink(string entityName, Guid entityId, OneToManyRegistry.OneToMany relationship, EntityRole? role, EntityReferenceCollection relatedEntities, bool set)
        {
            bool callerIsParent = role.HasValue
                ? role.Value == EntityRole.Referenced
                : string.Equals(entityName, relationship.ReferencedEntity, StringComparison.OrdinalIgnoreCase);
            foreach (EntityReference related in relatedEntities)
            {
                (string childEntity, Guid childId, EntityReference? parentRef) = callerIsParent
                    ? (related.LogicalName, related.Id, set ? new EntityReference(entityName, entityId) : null)
                    : (entityName, entityId, set ? related : null);

                Update(new Entity(childEntity, childId) { [relationship.ReferencingAttribute] = parentRef });
            }
        }

        /// <summary>
        /// Disassociates records over a many-to-many relationship by deleting its matching intersect rows. The
        /// relationship must be declared with <see cref="WithManyToMany"/>.
        /// </summary>
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            DemandStrictSecurityConfigured();

            // 1:N disassociate clears the child's lookup (an Update); only N:N removes intersect rows. Resolve first so
            // an undeclared relationship throws the standard guidance before the target-existence check.
            if (!TryResolveManyToMany(relationship.SchemaName, out RelationshipRegistry.ManyToMany rel))
            {
                if (_oneToMany.TryResolveBySchema(relationship.SchemaName, out OneToManyRegistry.OneToMany oneToMany))
                {
                    DemandRoleForReflexive(relationship, oneToMany.ReferencedEntity, oneToMany.ReferencingEntity);
                    DemandOneToManyAccess(entityName, entityId, oneToMany, relatedEntities);
                    DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
                    RunAtomically<object?>(() => { SetOneToManyLink(entityName, entityId, oneToMany, relationship.PrimaryEntityRole, relatedEntities, set: false); return null; });
                    return;
                }

                throw UnknownRelationship(relationship.SchemaName);
            }

            DemandRoleForReflexive(relationship, rel.FirstEntity, rel.SecondEntity);
            DemandManyToManyAccess(entityName, entityId, relatedEntities);
            DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName, relationship.PrimaryEntityRole);
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

        // The only tables Dataverse supports Merge on.
        private static readonly HashSet<string> MergeableEntities =
            new HashSet<string>(new[] { "account", "contact", "lead", "incident" }, StringComparer.OrdinalIgnoreCase);

        // Merge (account/contact/lead/incident): applies UpdateContent to the target (ignored for incident), reparents
        // the subordinate's 1:N children (Merge = Cascade) and moves its N:N associations to the target, then
        // deactivates the subordinate. The subordinate's own columns are NOT copied to the target (verified live, with
        // UpdateContent null or empty), and an inactive subordinate faults. All-or-nothing. The pipeline passes writeMaster to run the master's Update
        // through its Update steps; without it the store writes the master itself.
        internal void Merge(MergeRequest request, Action<Entity>? writeMaster)
        {
            EntityReference target = request.Target ?? throw new ArgumentNullException(nameof(request), "Merge Target is required.");
            string entity = target.LogicalName;
            Guid subordinateId = request.SubordinateId;

            if (!MergeableEntities.Contains(entity))
            {
                throw new InvalidPluginExecutionException(
                    $"Merge is not supported for '{entity}'. Dataverse supports Merge only for account, contact, lead and incident.");
            }

            if (!Table(entity).TryGetValue(target.Id, out Entity? targetRecord))
            {
                throw RecordDoesNotExist(entity, target.Id);
            }

            if (!Table(entity).TryGetValue(subordinateId, out Entity? subordinate))
            {
                throw RecordDoesNotExist(entity, subordinateId);
            }

            // Verified live: merging a subordinate that is already inactive (merged before) faults.
            if (subordinate!.GetAttributeValue<OptionSetValue>(SystemFieldApplier.StateCode)?.Value is int subordinateState && subordinateState != 0)
            {
                throw PlatformFault.Create(PlatformFault.MergeSubordinateInactive, $"sub-entity:{subordinateId} of type '{entity}' is deactive");
            }

            bool isIncident = string.Equals(entity, "incident", StringComparison.OrdinalIgnoreCase);

            RunAtomically<object?>(() =>
            {
                // 1. UpdateContent sets values on the target (ignored when merging incidents). Nothing else is written
                //    to the target: the subordinate's columns are not copied over (verified live).
                Entity merged = new Entity(entity, target.Id);
                if (request.UpdateContent != null && !isIncident)
                {
                    foreach (KeyValuePair<string, object> attribute in request.UpdateContent.Attributes)
                    {
                        if (!IsMergeSkippedColumn(entity, attribute.Key))
                        {
                            merged[attribute.Key] = EntityValues.CloneValue(attribute.Value);
                        }
                    }
                }

                if (merged.Attributes.Count > 0)
                {
                    if (writeMaster != null)
                    {
                        writeMaster(merged);
                    }
                    else
                    {
                        Update(merged);
                    }
                }

                // 2. Reparent the subordinate's 1:N children to the target, except over a relationship whose Merge
                //    behaviour is NoCascade (unset counts as Cascade, the platform's referential default), and move
                //    its N:N associations (verified live).
                foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ChildRelationshipsOf(entity)
                             .Where(relationship => relationship.Cascade.Merge != CascadeType.NoCascade).ToList())
                {
                    foreach (Entity child in Children(relationship, subordinateId).ToList())
                    {
                        Update(new Entity(child.LogicalName, child.Id)
                        {
                            [relationship.ReferencingAttribute] = new EntityReference(entity, target.Id),
                        });
                    }
                }

                MoveIntersectRows(entity, subordinateId, target.Id);

                // 3. Deactivate the subordinate (Inactive = statecode 1 / statuscode 2) and point its masterid at the
                //    target (verified live: the subordinate's post-image carries both).
                Update(new Entity(entity, subordinateId)
                {
                    ["statecode"] = new OptionSetValue(1),
                    ["statuscode"] = new OptionSetValue(2),
                    ["masterid"] = new EntityReference(entity, target.Id),
                });
                return null;
            });
        }

        private static bool IsMergeSkippedColumn(string entity, string column) =>
            string.Equals(column, entity + "id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "statecode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "statuscode", StringComparison.OrdinalIgnoreCase);

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

        // Captures ALL mutable stores so a transaction/operation rollback is complete: the record store, the sharing
        // (PoA) table, the access-team grants, the seeded rows still derived from the setup, the file blobs, and the
        // row-version counter. A partial snapshot would
        // leave a rolled-back batch with, say, a share or a file that its failed write had created. Auto-number
        // counters and open upload/download sessions are left out on purpose: the platform does not roll an
        // auto-number back either (a failed create still consumes its number), and a block upload is not part of
        // the transaction.
        internal StoreSnapshot Snapshot()
        {
            StoreSnapshot copy = new StoreSnapshot
            {
                Sharing = _sharing.Clone(),
                AccessTeams = _accessTeams.Clone(),
                SeededRows = new Dictionary<Guid, string>(_seededRows),
                Version = _version,
            };

            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in _store)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                copy.Store[table.Key] = inner;
            }

            foreach (KeyValuePair<Guid, FileBlob> file in _files)
            {
                copy.Files[file.Key] = CloneFileBlob(file.Value);
            }

            return copy;
        }

        internal void RestoreTo(StoreSnapshot snapshot)
        {
            _store.Clear();
            foreach (KeyValuePair<string, Dictionary<Guid, Entity>> table in snapshot.Store)
            {
                Dictionary<Guid, Entity> inner = new Dictionary<Guid, Entity>();
                foreach (KeyValuePair<Guid, Entity> record in table.Value)
                {
                    inner[record.Key] = EntityValues.Clone(record.Value);
                }

                _store[table.Key] = inner;
            }

            _sharing.CopyFrom(snapshot.Sharing);
            _accessTeams.CopyFrom(snapshot.AccessTeams);

            // A rolled-back cascade gives the seeded rows it reached back to the setup.
            _seededRows = new Dictionary<Guid, string>(snapshot.SeededRows);

            _files.Clear();
            foreach (KeyValuePair<Guid, FileBlob> file in snapshot.Files)
            {
                _files[file.Key] = CloneFileBlob(file.Value);
            }

            _version = snapshot.Version;
        }

        private static FileBlob CloneFileBlob(FileBlob blob) => new FileBlob
        {
            Content = (byte[])blob.Content.Clone(),
            FileName = blob.FileName,
            MimeType = blob.MimeType,
            EntityLogicalName = blob.EntityLogicalName,
            RecordId = blob.RecordId,
            AttributeName = blob.AttributeName,
        };

        // A point-in-time deep copy of every mutable store, taken by Snapshot() and re-applied by RestoreTo() so an
        // ExecuteTransaction (or any pipeline operation) rolls back cleanly. Nested so it can reference FileBlob.
        internal sealed class StoreSnapshot
        {
            public Dictionary<string, Dictionary<Guid, Entity>> Store { get; } =
                new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<Guid, FileBlob> Files { get; } = new Dictionary<Guid, FileBlob>();

            public SharingTable Sharing { get; set; } = new SharingTable();

            public AccessTeamGrants AccessTeams { get; set; } = new AccessTeamGrants();

            public Dictionary<Guid, string> SeededRows { get; set; } = new Dictionary<Guid, string>();

            public long Version { get; set; }
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
                MimeType = TryGetParam<string>(request, "MimeType") ?? "application/octet-stream",
                EntityLogicalName = session.EntityLogicalName,
                RecordId = session.RecordId,
                AttributeName = session.AttributeName,
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

            if (!Table(target.LogicalName).TryGetValue(target.Id, out Entity? record))
            {
                throw RecordDoesNotExist(target.LogicalName, target.Id);
            }

            // A file download must respect the same access control as Retrieve: a record-level Read demand and
            // field-level security on the File column. Without this a caller with no Read (or no access to a secured
            // File column) could stream content that Retrieve/RetrieveMultiple would have masked - a false green.
            if (!MembershipIntersectTables.Contains(target.LogicalName))
            {
                DemandAccess(target.LogicalName, Privilege.Read, record);
            }

            DemandSecuredColumnRead(target.LogicalName, attribute);
            DemandFullImageStored(target, attribute, record);

            if (!record.Contains(attribute) || record[attribute] == null)
            {
                // The platform's "nothing to download" fault (seen live on an Image column with no full image; not
                // probed on an empty File column).
                throw PlatformFault.Create(ObjectDoesNotExist,
                    $"No FileAttachment records found for '{target.LogicalName}.{attribute}' on record {target.Id}.");
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

        // The platform keeps only a thumbnail for an Image column that does not store full images, so the block
        // download has nothing to serve and fails with 0x80040217 (verified live, 9.2.26094.133; account.entityimage
        // is such a column). Only registered metadata tells an Image column from inline bytes, so without an
        // ImageAttributeMetadata for the column the download keeps serving the stored bytes. The platform applies
        // the setting in force when the image was set; the harness reads it at download time.
        private void DemandFullImageStored(EntityReference target, string attribute, Entity record)
        {
            if (!_metadata.TryGetValue(target.LogicalName, out EntityMetadata? metadata)
                || metadata.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, attribute, StringComparison.OrdinalIgnoreCase))
                    is not ImageAttributeMetadata image
                || image.CanStoreFullImage == true)
            {
                return;
            }

            Guid descriptorId = record.GetAttributeValue<Guid>(attribute + "id");
            string message =
                $"No FileAttachment records found for imagedescriptorId: {descriptorId:D} for image attribute: {attribute} " +
                $"of {target.LogicalName} record with id {target.Id:D}";
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = ObjectDoesNotExist, Message = message },
                new FaultReason(message));
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
            // A file whose record is gone is unknown too (one uploaded to a record deleted mid-upload): without the
            // record there is nothing to check Write against.
            Entity? record = _files.TryGetValue(fileId, out FileBlob? blob) ? StoredOrNull(blob.EntityLogicalName, blob.RecordId) : null;
            if (blob == null || record == null)
            {
                throw PlatformFault.Create(ObjectDoesNotExist, $"File with Id = {fileId:D} Does Not Exist");
            }

            // Deleting a file clears its File column: it needs Write on that record (and on the column when it is
            // secured), and touches no other column or table, even one holding the same Guid.
            DemandAccess(blob.EntityLogicalName, Privilege.Write, record);
            DemandSecuredColumnWrites(new Entity(blob.EntityLogicalName, blob.RecordId) { [blob.AttributeName] = null }, onCreate: false);
            if (record.GetAttributeValue<Guid?>(blob.AttributeName) == fileId)
            {
                record.Attributes.Remove(blob.AttributeName);
            }

            _files.Remove(fileId);
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

        internal sealed class FileBlob
        {
            public byte[] Content = Array.Empty<byte>();
            public string FileName = string.Empty;
            public string MimeType = string.Empty;

            // The File column the blob was uploaded to: DeleteFile clears that column only.
            public string EntityLogicalName = string.Empty;
            public Guid RecordId;
            public string AttributeName = string.Empty;
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
                    // Deep-copy each value: the AllColumns branch clones, and a projected read must be just as
                    // isolated, so mutating a returned mutable value can't corrupt the stored record.
                    projected[column] = EntityValues.CloneValue(entity[column]);
                }
            }

            return projected;
        }
    }
}
