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

        // Record-level shares (PoA). Always present so Grant/RetrievePrincipalAccess work with or without a
        // security model; only consulted for enforcement when _security is set.
        private readonly SharingTable _sharing = new SharingTable();

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
            _security = configuration.Build(_sharing);
            MaterializeSecurityTables();
            return this;
        }

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

        /// <summary>
        /// Pre-shares a record with a principal (arrange helper, like <see cref="Seed"/>): grants the principal
        /// (a user or team) the given <paramref name="rights"/> on the target, without going through the
        /// GrantAccess message or its Share-privilege check. Only takes effect once <see cref="WithSecurity"/> is set.
        /// </summary>
        public InMemoryOrganizationService WithSharedRecord(EntityReference target, EntityReference principal, AccessRights rights)
        {
            _sharing.Grant(target, new PrincipalAccess { Principal = principal, AccessMask = rights });
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
        /// Declares a one-to-many (1:N) relationship and its cascade behaviour, so Delete (and later Assign/Share)
        /// cascade from the parent (<paramref name="referencedEntity"/>) to the children
        /// (<paramref name="referencingEntity"/> rows whose <paramref name="referencingAttribute"/> lookup points at
        /// the parent). Use <see cref="WithParentalRelationship"/>/<see cref="WithReferentialRelationship"/> for the
        /// common presets.
        /// </summary>
        public InMemoryOrganizationService WithRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, CascadeConfiguration cascade)
        {
            if (string.IsNullOrWhiteSpace(referencedEntity) || string.IsNullOrWhiteSpace(referencingEntity) || string.IsNullOrWhiteSpace(referencingAttribute))
            {
                throw new ArgumentException("Referenced/referencing entity and the referencing attribute are required.");
            }

            _oneToMany.Add(new OneToManyRegistry.OneToMany(
                $"{referencedEntity}_{referencingEntity}", referencedEntity, referencingEntity, referencingAttribute, cascade));
            return this;
        }

        /// <summary>Declares a <b>parental</b> 1:N relationship: every action (incl. Delete) cascades to children.</summary>
        public InMemoryOrganizationService WithParentalRelationship(string referencedEntity, string referencingEntity, string referencingAttribute) =>
            WithRelationship(referencedEntity, referencingEntity, referencingAttribute, new CascadeConfiguration
            {
                Assign = CascadeType.Cascade,
                Delete = CascadeType.Cascade,
                Merge = CascadeType.Cascade,
                Reparent = CascadeType.Cascade,
                Share = CascadeType.Cascade,
                Unshare = CascadeType.Cascade,
            });

        /// <summary>
        /// Declares a <b>referential</b> 1:N relationship: nothing cascades except Delete, which removes the child's
        /// link. Set <paramref name="restrictDelete"/> to block deleting the parent while children exist.
        /// </summary>
        public InMemoryOrganizationService WithReferentialRelationship(string referencedEntity, string referencingEntity, string referencingAttribute, bool restrictDelete = false) =>
            WithRelationship(referencedEntity, referencingEntity, referencingAttribute, new CascadeConfiguration
            {
                Assign = CascadeType.NoCascade,
                Delete = restrictDelete ? CascadeType.Restrict : CascadeType.RemoveLink,
                Merge = CascadeType.NoCascade,
                Reparent = CascadeType.NoCascade,
                Share = CascadeType.NoCascade,
                Unshare = CascadeType.NoCascade,
            });

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
            DemandCreate(entity);
            DemandLookupAppends(entity, null);

            Entity stored = EntityValues.Clone(entity);
            if (stored.Id == Guid.Empty)
            {
                stored.Id = _idSequence();
            }

            _autoNumbers.Apply(stored);
            _systemFields.ApplyOnCreate(entity, stored, Actor(), _clock(), NextVersion());
            RealignOwningBusinessUnit(stored);
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

            if (!SecurityInfraTables.Contains(entityName))
            {
                DemandAccess(entityName, Privilege.Read, stored);
            }

            return Project(stored, columnSet);
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
            if (!Table(entity.LogicalName).TryGetValue(entity.Id, out Entity? stored))
            {
                throw new InvalidOperationException($"{entity.LogicalName} with id {entity.Id} does not exist in the in-memory store.");
            }

            bool changesOwner = entity.Contains(SystemFieldApplier.OwnerId);
            // Captured before the merge: needed for the UserOwned cascade scope (children owned by the same user).
            EntityReference? previousOwner = changesOwner ? stored.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) : null;
            if (entity.Attributes.Keys.Any(key => !string.Equals(key, SystemFieldApplier.OwnerId, StringComparison.OrdinalIgnoreCase)))
            {
                DemandAccess(entity.LogicalName, Privilege.Write, stored);
            }

            if (changesOwner)
            {
                DemandAccess(entity.LogicalName, Privilege.Assign, stored);
            }

            DemandLookupAppends(entity, stored);

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

        // Enforces a privilege for the current user when security is configured; a no-op otherwise (default-off).
        // Access is the union of role access, sharing (both in the SecurityModel) and inherited access from a
        // parental parent record — so a user who can act on the parent can act on the child.
        private void DemandAccess(string entityName, Privilege privilege, Entity? record)
        {
            if (_security == null || CanAccess(entityName, privilege, record))
            {
                return;
            }

            throw _security.PrivilegeDenied(_currentUserId, privilege, entityName);
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
            if (_security == null)
            {
                return;
            }

            EntityReference owner =
                entity.Contains(SystemFieldApplier.OwnerId) && entity[SystemFieldApplier.OwnerId] is EntityReference explicitOwner
                    ? explicitOwner
                    : new EntityReference("systemuser", _currentUserId);

            Entity prospective = new Entity(entity.LogicalName) { [SystemFieldApplier.OwnerId] = owner };
            Guid ownerBusinessUnit = _security.BusinessUnitOfPrincipal(owner);
            if (ownerBusinessUnit != Guid.Empty)
            {
                prospective[SystemFieldApplier.OwningBusinessUnit] = new EntityReference("businessunit", ownerBusinessUnit);
            }

            _security.Demand(_currentUserId, entity.LogicalName, Privilege.Create, prospective);

            bool ownedByCaller = string.Equals(owner.LogicalName, "systemuser", StringComparison.OrdinalIgnoreCase) && owner.Id == _currentUserId;
            if (!ownedByCaller)
            {
                _security.Demand(_currentUserId, entity.LogicalName, Privilege.Assign, null);
            }
        }

        // Relating records needs AppendTo on the record being related to and Append on each record related to it,
        // mirroring the platform. Off (or nothing configured) is a no-op.
        private void DemandAssociationAccess(string entityName, Guid entityId, EntityReferenceCollection relatedEntities)
        {
            if (_security == null)
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

        // Setting a lookup relates two records, which needs Append on the record carrying the lookup and AppendTo on
        // the lookup's target record (Dataverse). No-op when security is off. <paramref name="self"/> is the record
        // carrying the lookup (the stored row on Update; null on Create, where it does not exist yet).
        private void DemandLookupAppends(Entity entity, Entity? self)
        {
            if (_security == null)
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
            // no-op (unchanged behaviour).
            AccessRights sharerRights = EffectiveAccess(new EntityReference("systemuser", _currentUserId), target);
            return new PrincipalAccess { Principal = access.Principal, AccessMask = access.AccessMask & sharerRights };
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
            if (_security != null && Table(entityName).TryGetValue(id, out Entity? stored))
            {
                DemandAccess(entityName, Privilege.Delete, stored);
            }

            CascadeDelete(entityName, id, new HashSet<Guid>(), removeRecord);
        }

        // Removes a single record from the store with no cascade — the default remover, and the primitive the
        // pipeline's remover ends with after firing the record's Delete plugins.
        internal void RemoveRecordDirect(string entityName, Guid id) => Table(entityName).Remove(id);

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
                    throw new InvalidOperationException(
                        $"Cannot delete {entityName} {id}: related {relationship.ReferencingEntity} records exist and the relationship's Delete behaviour is Restrict.");
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

            removeRecord(entityName, id);
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

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            // With security on, the read filter is applied inside the engine to the primary rows BEFORE paging, so
            // pages are drawn from the readable set (not trimmed afterwards). Aggregates are not filtered yet.
            InMemoryQueryEngine engine = new InMemoryQueryEngine(name => Table(name).Values, _currentUserId, ReadFilter());
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

        // The predicate the query engine uses to keep only rows the current user may read (null = no security).
        // Includes access inherited from a parental parent, and reads the stored record's own owner columns so
        // filtering is correct regardless of the query's ColumnSet.
        private Func<Entity, bool>? ReadFilter() =>
            _security == null ? null : row => SecurityInfraTables.Contains(row.LogicalName) || CanAccess(row.LogicalName, Privilege.Read, row);

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
                    // Modern Dataverse models assignment as an ownerid Update; UpdateCore infers the Assign
                    // privilege from the ownerid change, so both paths enforce identically.
                    UpdateCore(new Entity(assign.Target.LogicalName, assign.Target.Id) { ["ownerid"] = assign.Assignee });
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

                case WhoAmIRequest:
                {
                    SystemActor actor = Actor();
                    WhoAmIResponse response = new WhoAmIResponse();
                    response.Results["UserId"] = actor.UserId;
                    response.Results["BusinessUnitId"] = actor.BusinessUnitId;
                    response.Results["OrganizationId"] = actor.OrganizationId;
                    return response;
                }

                case GrantAccessRequest grant:
                {
                    DemandAccess(grant.Target.LogicalName, Privilege.Share, StoredOrNull(grant.Target.LogicalName, grant.Target.Id));
                    PrincipalAccess granted = ClampToSharerRights(grant.Target, grant.PrincipalAccess);
                    _sharing.Grant(grant.Target, granted);
                    CascadeShare(grant.Target.LogicalName, grant.Target.Id, granted, new HashSet<Guid>());
                    return new GrantAccessResponse();
                }

                case ModifyAccessRequest modify:
                {
                    DemandAccess(modify.Target.LogicalName, Privilege.Share, StoredOrNull(modify.Target.LogicalName, modify.Target.Id));
                    PrincipalAccess modified = ClampToSharerRights(modify.Target, modify.PrincipalAccess);
                    _sharing.Modify(modify.Target, modified);
                    CascadeShare(modify.Target.LogicalName, modify.Target.Id, modified, new HashSet<Guid>());
                    return new ModifyAccessResponse();
                }

                case RevokeAccessRequest revoke:
                {
                    DemandAccess(revoke.Target.LogicalName, Privilege.Share, StoredOrNull(revoke.Target.LogicalName, revoke.Target.Id));
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
            DemandAssociationAccess(entityName, entityId, relatedEntities);
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
            DemandAssociationAccess(entityName, entityId, relatedEntities);
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
