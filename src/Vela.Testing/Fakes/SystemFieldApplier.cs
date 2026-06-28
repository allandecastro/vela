using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

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
    /// <c>AsVirtual</c>/<c>AsElastic</c>) wins; otherwise it is inferred from the early-bound type (a generated class
    /// exposes an <c>OwnerId</c> property only for user/team-owned tables); late-bound rows with no declaration default
    /// to user-owned. Values the caller already set are never overwritten on Create.
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
        internal const string OrganizationId = "organizationid";

        private const string SystemUser = "systemuser";
        private const string Team = "team";
        private const string BusinessUnit = "businessunit";
        private const string Organization = "organization";

        private readonly Dictionary<string, OwnershipKind> _declared =
            new Dictionary<string, OwnershipKind>(StringComparer.OrdinalIgnoreCase);

        public void Declare(string entityLogicalName, OwnershipKind kind)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
            {
                throw new ArgumentException("Entity logical name is required.", nameof(entityLogicalName));
            }

            _declared[entityLogicalName] = kind;
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

            // overriddencreatedon backdates createdon (e.g. data migrations), exactly as the platform does.
            if (!stored.Contains(CreatedOn) && stored.Contains(OverriddenCreatedOn) && stored[OverriddenCreatedOn] is DateTime overridden)
            {
                stored[CreatedOn] = overridden;
            }

            SetIfAbsent(stored, CreatedOn, now);
            SetIfAbsent(stored, CreatedBy, caller);
            SetIfAbsent(stored, ModifiedOn, now);
            SetIfAbsent(stored, ModifiedBy, caller);
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

            // Early-bound generated classes expose an OwnerId property only for user/team-owned tables, so a
            // typed instance lets us infer ownership with no configuration. Late-bound rows default to user-owned.
            if (source != null && source.GetType() != typeof(Entity))
            {
                return source.GetType().GetProperty(nameof(OwnerId)) != null
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
        public SystemActor(Guid userId, Guid businessUnitId, Guid organizationId)
        {
            UserId = userId;
            BusinessUnitId = businessUnitId;
            OrganizationId = organizationId;
        }

        public Guid UserId { get; }

        public Guid BusinessUnitId { get; }

        public Guid OrganizationId { get; }
    }
}
