using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Testing
{
    /// <summary>
    /// The ownership/storage type of a Dataverse table, which decides the standard columns the platform
    /// auto-populates. See <see cref="SystemFieldApplier"/>.
    /// </summary>
    public enum OwnershipKind
    {
        /// <summary>User or team owned: carries <c>ownerid</c> + <c>owninguser</c>/<c>owningteam</c> + <c>owningbusinessunit</c>.</summary>
        UserOwned,

        /// <summary>Organization owned: no owner columns (audit columns only).</summary>
        OrganizationOwned,

        /// <summary>Virtual (external data) table: the platform owns no rows, so no system columns are populated.</summary>
        Virtual,

        /// <summary>
        /// Elastic table. Elastic is a storage type, not an ownership type — elastic tables are ownable, so they
        /// carry the same audit and owner columns as a user/team-owned table.
        /// </summary>
        Elastic,
    }

    /// <summary>
    /// Populates the standard Dataverse system columns the platform sets for you, so plugins that read
    /// <c>createdby</c>/<c>ownerid</c>/etc. or filter on them behave like they do online. On Create it sets
    /// <c>createdon</c>/<c>createdby</c>/<c>modifiedon</c>/<c>modifiedby</c> and, for user/team-owned tables, the
    /// owner columns (<c>ownerid</c>, <c>owninguser</c> or <c>owningteam</c>, <c>owningbusinessunit</c>). On Update
    /// it refreshes <c>modifiedon</c>/<c>modifiedby</c> and re-derives the owner columns when <c>ownerid</c> changes.
    /// </summary>
    /// <remarks>
    /// The ownership type is resolved per table: an explicit declaration (<c>AsOrganizationOwned</c>/<c>AsUserOwned</c>/
    /// <c>AsVirtual</c>/<c>AsElastic</c>) wins; then the <c>OwnershipType</c> of the table's registered metadata
    /// (<c>WithEntityMetadata</c>); then the early-bound type (a generated class exposes an <c>OwnerId</c> property only
    /// for user/team-owned tables), also when the pipeline handed the store a plain copy of it; late-bound rows with
    /// none of these default to user-owned. The audit columns and <c>owningbusinessunit</c> are always stamped (a value
    /// the caller supplies is ignored, as on Dataverse); <c>ownerid</c> is kept when the caller sets it.
    /// </remarks>
    internal sealed class SystemFieldApplier
    {
        internal const string CreatedOn = "createdon";
        internal const string CreatedBy = "createdby";
        internal const string ModifiedOn = "modifiedon";
        internal const string ModifiedBy = "modifiedby";
        internal const string OverriddenCreatedOn = "overriddencreatedon";
        internal const string StateCode = "statecode";
        internal const string StatusCode = "statuscode";
        internal const string VersionNumber = "versionnumber";
        internal const string OwnerId = "ownerid";
        internal const string OwningUser = "owninguser";
        internal const string OwningTeam = "owningteam";
        internal const string OwningBusinessUnit = "owningbusinessunit";
        internal const string CreatedOnBehalfBy = "createdonbehalfby";
        internal const string OrganizationId = "organizationid";

        private const string SystemUser = "systemuser";
        private const string Team = "team";
        private const string BusinessUnit = "businessunit";
        private const string Organization = "organization";

        // Whether an early-bound type exposes an OwnerId property, per type: a type's members never change, and the
        // ownership inference reads it on every write of a typed record.
        private static readonly ConcurrentDictionary<Type, bool> EarlyBoundOwnable = new ConcurrentDictionary<Type, bool>();

        private readonly Dictionary<string, OwnershipKind> _declared =
            new Dictionary<string, OwnershipKind>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, OwnershipKind> _registered =
            new Dictionary<string, OwnershipKind>(StringComparer.OrdinalIgnoreCase);

        public void Declare(string entityLogicalName, OwnershipKind kind)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
            {
                throw new ArgumentException("Entity logical name is required.", nameof(entityLogicalName));
            }

            _declared[entityLogicalName] = kind;
        }

        // The ownership the table's registered metadata states (WithEntityMetadata). A later registration replaces it;
        // an OwnershipType that names no ownership (None, or not set) clears it.
        public void Register(string entityLogicalName, OwnershipTypes? ownership)
        {
            OwnershipKind? kind = FromMetadata(ownership);
            if (kind == null)
            {
                _registered.Remove(entityLogicalName);
            }
            else
            {
                _registered[entityLogicalName] = kind.Value;
            }
        }

        private static OwnershipKind? FromMetadata(OwnershipTypes? ownership)
        {
            if (ownership == null)
            {
                return null;
            }

            if ((ownership.Value & OwnershipTypes.OrganizationOwned) != 0)
            {
                return OwnershipKind.OrganizationOwned;
            }

            if ((ownership.Value & (OwnershipTypes.UserOwned | OwnershipTypes.TeamOwned)) != 0)
            {
                return OwnershipKind.UserOwned;
            }

            return null;
        }

        /// <summary>Populates create-time system columns on <paramref name="stored"/> (the record about to be saved).</summary>
        public void ApplyOnCreate(Entity original, Entity stored, SystemActor actor, DateTime now, long version)
        {
            OwnershipKind kind = Resolve(original, stored.LogicalName);
            if (kind == OwnershipKind.Virtual)
            {
                return;
            }

            EntityReference caller = new EntityReference(SystemUser, actor.UserId);

            // Verified live: the audit columns and owningbusinessunit a caller supplies on Create are silently ignored
            // and stamped from the actual time, caller and owner. overriddencreatedon=X backdates createdon to X (a data
            // migration) and itself records the actual creation time.
            stored[CreatedOn] = stored.GetAttributeValue<object>(OverriddenCreatedOn) is DateTime overridden ? overridden : now;
            if (stored.Contains(OverriddenCreatedOn) && stored[OverriddenCreatedOn] is DateTime)
            {
                stored[OverriddenCreatedOn] = now;
            }

            stored[CreatedBy] = caller;
            stored[ModifiedOn] = now;
            stored[ModifiedBy] = caller;
            stored.Attributes.Remove(OwningBusinessUnit);
            if (actor.OnBehalfOfUserId is Guid onBehalfOf)
            {
                // Verified live: a write through the SYSTEM service records the calling user here.
                stored[CreatedOnBehalfBy] = new EntityReference(SystemUser, onBehalfOf);
            }

            // Verified live: a statecode supplied without a statuscode is ignored; the record is created in the default
            // state and status.
            if (stored.Contains(StateCode) && !(stored.Contains(StatusCode) && stored[StatusCode] != null))
            {
                stored.Attributes.Remove(StateCode);
            }

            SetIfAbsent(stored, StateCode, new OptionSetValue(0));
            SetIfAbsent(stored, StatusCode, new OptionSetValue(1));
            SetIfAbsent(stored, VersionNumber, version);

            if (IsOwnable(kind))
            {
                ApplyOwner(stored, actor);
            }
            else if (kind == OwnershipKind.OrganizationOwned)
            {
                SetIfAbsent(stored, OrganizationId, new EntityReference(Organization, actor.OrganizationId));
            }
        }

        /// <summary>
        /// Whether the table is declared organization-owned (<c>AsOrganizationOwned</c>) or registered so by its metadata,
        /// so it has no <c>ownerid</c> column. The early-bound inference is left out: a hand-written test class without an
        /// <c>OwnerId</c> property does not make a write fail.
        /// </summary>
        public bool IsDeclaredElastic(string logicalName) =>
            _declared.TryGetValue(logicalName, out OwnershipKind declared) && declared == OwnershipKind.Elastic;

        public bool IsKnownOrganizationOwned(string logicalName) =>
            (_declared.TryGetValue(logicalName, out OwnershipKind declared)
                || _registered.TryGetValue(logicalName, out declared))
            && declared == OwnershipKind.OrganizationOwned;

        /// <summary>Refreshes modified columns and re-derives owner columns when the update changed the owner.</summary>
        public void ApplyOnUpdate(Entity update, Entity stored, SystemActor actor, DateTime now, long version)
        {
            OwnershipKind kind = Resolve(update, stored.LogicalName);
            if (kind == OwnershipKind.Virtual)
            {
                return;
            }

            // Dataverse stamps modified* and bumps the row version on EVERY update and ignores any caller-
            // supplied values for these server-managed columns. (To pin modifiedon deterministically in a
            // test, inject the clock via WithClock rather than setting the attribute.)
            stored[ModifiedOn] = now;
            stored[ModifiedBy] = new EntityReference(SystemUser, actor.UserId);
            stored[VersionNumber] = version;

            if (IsOwnable(kind) && update.Contains(OwnerId))
            {
                ApplyOwner(stored, actor);
            }
        }

        // User/team-owned and elastic tables carry owner columns; organization-owned and virtual do not.
        private static bool IsOwnable(OwnershipKind kind) =>
            kind == OwnershipKind.UserOwned || kind == OwnershipKind.Elastic;

        // Sets ownerid (when missing) and re-derives owninguser/owningteam + owningbusinessunit from it.
        private static void ApplyOwner(Entity stored, SystemActor actor)
        {
            if (!stored.Contains(OwnerId) || stored[OwnerId] is not EntityReference owner)
            {
                owner = new EntityReference(SystemUser, actor.UserId);
                stored[OwnerId] = owner;
            }

            if (string.Equals(owner.LogicalName, Team, StringComparison.OrdinalIgnoreCase))
            {
                stored[OwningTeam] = new EntityReference(Team, owner.Id);
                stored.Attributes.Remove(OwningUser);
            }
            else
            {
                stored[OwningUser] = new EntityReference(SystemUser, owner.Id);
                stored.Attributes.Remove(OwningTeam);
            }

            if (actor.BusinessUnitId != Guid.Empty && !stored.Contains(OwningBusinessUnit))
            {
                stored[OwningBusinessUnit] = new EntityReference(BusinessUnit, actor.BusinessUnitId);
            }
        }

        private OwnershipKind Resolve(Entity source, string logicalName)
        {
            if (_declared.TryGetValue(logicalName, out OwnershipKind declared))
            {
                return declared;
            }

            if (_registered.TryGetValue(logicalName, out OwnershipKind registered))
            {
                return registered;
            }

            // Early-bound generated classes expose an OwnerId property only for user/team-owned tables, so a
            // typed instance lets us infer ownership with no configuration. The pipeline hands the store a plain copy
            // of the target, which still carries the type it was copied from. Late-bound rows default to user-owned.
            Type? earlyBound = source == null ? null : EntityValues.EarlyBoundType(source);
            if (earlyBound != null)
            {
                return EarlyBoundOwnable.GetOrAdd(earlyBound, type => type.GetProperty(nameof(OwnerId)) != null)
                    ? OwnershipKind.UserOwned
                    : OwnershipKind.OrganizationOwned;
            }

            return OwnershipKind.UserOwned;
        }

        private static void SetIfAbsent(Entity entity, string attribute, object value)
        {
            if (!entity.Contains(attribute) || entity[attribute] == null)
            {
                entity[attribute] = value;
            }
        }
    }

    /// <summary>The calling identity stamped into system columns: user, business unit and organization.</summary>
    internal readonly struct SystemActor
    {
        public SystemActor(Guid userId, Guid businessUnitId, Guid organizationId, Guid? onBehalfOfUserId = null)
        {
            UserId = userId;
            BusinessUnitId = businessUnitId;
            OrganizationId = organizationId;
            OnBehalfOfUserId = onBehalfOfUserId;
        }

        /// <summary>The calling user a SYSTEM write is made for (stamped into <c>createdonbehalfby</c>), else null.</summary>
        public Guid? OnBehalfOfUserId { get; }

        public Guid UserId { get; }

        public Guid BusinessUnitId { get; }

        public Guid OrganizationId { get; }
    }
}
