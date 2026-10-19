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
    // Store configuration forwarders: each declares part of the scenario on the in-memory store the pipeline drives.
    public sealed partial class PluginPipeline
    {
        /// <summary>
        /// Opts into strict security (see <c>InMemoryOrganizationService.WithStrictSecurity</c>): an operation run without
        /// a configured security model throws instead of silently granting full access.
        /// </summary>
        public PluginPipeline WithStrictSecurity()
        {
            _store.WithStrictSecurity();
            return this;
        }

        /// <summary>
        /// Registers an access-team template and the rights it grants (see
        /// <c>InMemoryOrganizationService.WithAccessTeamTemplate</c>). Enforced once <see cref="WithSecurity"/> is set.
        /// </summary>
        public PluginPipeline WithAccessTeamTemplate(Guid templateId, Microsoft.Crm.Sdk.Messages.AccessRights rights)
        {
            _store.WithAccessTeamTemplate(templateId, rights);
            return this;
        }

        /// <summary>
        /// Pre-shares a record with a user or team (see <c>InMemoryOrganizationService.WithSharedRecord</c>), without
        /// going through GrantAccess. Takes effect once <see cref="WithSecurity"/> is set.
        /// </summary>
        public PluginPipeline WithSharedRecord(EntityReference target, EntityReference principal, Microsoft.Crm.Sdk.Messages.AccessRights rights)
        {
            _store.WithSharedRecord(target, principal, rights);
            return this;
        }

        /// <summary>Declares an alternate key on the in-memory store (duplicate-key throws).</summary>
        public PluginPipeline WithAlternateKey(string entityLogicalName, params string[] keyAttributes)
        {
            _store.WithAlternateKey(entityLogicalName, keyAttributes);
            return this;
        }

        /// <summary>
        /// Declares the default status reason of a state on the in-memory store (see
        /// <c>InMemoryOrganizationService.WithDefaultStatus</c>): the status a record gets from a SetState with status -1
        /// or an Update of statecode alone. Undeclared states use state 0 -> status 1, any other state -> status 2.
        /// </summary>
        public PluginPipeline WithDefaultStatus(string entityLogicalName, int state, int status)
        {
            _store.WithDefaultStatus(entityLogicalName, state, status);
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

        /// <summary>
        /// Overrides the pipeline's clock (default <see cref="DateTime.UtcNow"/>): it stamps <c>createdon</c> /
        /// <c>modifiedon</c> and expires the plug-in cache (<c>IPluginCacheService</c>), so a test moves time past a
        /// TTL instead of sleeping. The pipeline has a cache of its own, kept across its executions.
        /// </summary>
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

        /// <summary>
        /// Replaces a service the plugins register, for this test: every plugin and Custom API the pipeline runs (the
        /// nested executions included) resolves <paramref name="instance"/> for <typeparamref name="TService"/> (the
        /// same instance for the whole execution), whatever its <c>ConfigureServices</c> or the assembly's
        /// <c>[assembly: VelaServiceRegistrar]</c> register for that type. It applies to Vela's built-in resolver
        /// (<c>ctx.GetRequiredService&lt;T&gt;()</c>), not to a container a plugin builds itself. Calling it again for
        /// the same type replaces the earlier instance. A platform service the context reads from the platform
        /// (<see cref="IManagedIdentityService"/>, <see cref="ITracingService"/>, the telemetry <c>ILogger</c>,
        /// <see cref="IOrganizationService"/>, <see cref="IOrganizationServiceFactory"/>, the execution context) is
        /// rejected with an <see cref="ArgumentException"/> naming how the pipeline supplies it.
        /// </summary>
        public PluginPipeline WithService<TService>(TService instance) where TService : class
        {
            _serviceOverrides.Add(instance);
            return this;
        }

        /// <summary>
        /// Registers a managed-identity service for every plugin and Custom API the pipeline runs (the nested
        /// executions included), so <c>ctx.AcquireManagedIdentityToken</c> and <c>ctx.ManagedIdentityService</c> use
        /// it. Without it the pipeline serves none, as on an environment without a managed identity. Calling it again
        /// replaces the earlier service.
        /// </summary>
        public PluginPipeline WithManagedIdentityService(IManagedIdentityService service)
        {
            _managedIdentityService = service ?? throw new ArgumentNullException(nameof(service));
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
    }
}
