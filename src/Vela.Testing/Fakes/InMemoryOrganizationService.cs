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
    /// Not thread-safe: use one instance per test, from one thread.
    /// This type owns the store and the platform-behaviour configuration (alternate keys, auto-numbers). The
    /// read side (query execution) lives in <see cref="InMemoryQueryEngine"/>; scalar value handling shared by
    /// both is in <see cref="EntityValues"/>. This file holds the configuration, seeding and the Execute dispatch;
    /// each message family has its own partial (Crud, Queries, Relationships, AssignStateMerge, Transactions,
    /// Files, Security, ReadShape).
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

        // Whether the table was declared elastic (AsElastic): only an elastic table has an Upsert message filter.
        internal bool IsElastic(string entityLogicalName) => _systemFields.IsDeclaredElastic(entityLogicalName);

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
                    UpdateCore(new Entity(assign.Target.LogicalName, ResolveReference(assign.Target)) { ["ownerid"] = assign.Assignee }, readCheck: false);
                    return new AssignResponse();
                }

                case SetStateRequest setState:
                {
                    // The platform runs SetState as an Update of statecode/statuscode, with status -1 resolved to the
                    // state's default status.
                    DemandPlatformRead(setState.EntityMoniker.LogicalName, ResolveReference(setState.EntityMoniker));
                    UpdateCore(SetStateUpdate(setState.EntityMoniker, setState.State, setState.Status), readCheck: false);
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
                    DemandTemplatePrivileges(addToTeam.Record.LogicalName, addToTeam.SystemUserId, rights);
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
