using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Vela.Messages
{
    // Vela-owned typed message requests (typed-requests design). Unlike the SDK request types (sealed, Target
    // typed as the base Entity), a Vela request is generic over the entity, so ctx.Request exposes an early-bound
    // Target - the single typed accessor for the primary record. The Target getter mirrors GetTarget<T>() exactly
    // (Parameters["Target"].ToEntity<TEntity>()). See .context/typed-requests-design.md.

    /// <summary>
    /// Base class for a Vela-owned typed request. Each typed property is a one-liner over the underlying
    /// <c>Parameters</c> bag: <c>public Guid AccountId { get =&gt; Get&lt;Guid&gt;(); set =&gt; Set(value); }</c>.
    /// The parameter key defaults to the property name (via <see cref="CallerMemberNameAttribute"/>), so authored
    /// and generated request classes need no <c>Parameters.Contains(...)</c> boilerplate.
    /// </summary>
    public abstract class TypedRequest : OrganizationRequest
    {
        /// <summary>Reads a typed value from <c>Parameters</c> (defaults when absent). Key defaults to the property name.</summary>
        /// <typeparam name="T">The parameter's CLR type.</typeparam>
        /// <param name="name">The parameter key; defaults to the calling property's name.</param>
        protected virtual T Get<T>([CallerMemberName] string name = "") => Parameters.Contains(name) && Parameters[name] is T typed ? typed : default!;

        /// <summary>Writes a typed value into <c>Parameters</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The parameter's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The parameter key; defaults to the calling property's name.</param>
        protected virtual void Set<T>(T value, [CallerMemberName] string name = "") => Parameters[name] = value!;
    }

    /// <summary>
    /// Base class for a typed <b>Custom API</b> request. Dataverse names a custom API's request parameters
    /// <c>{apiUniqueName}.{paramName}</c> (that is the value exposed by the OData operation and used in the
    /// plugin's <c>InputParameters</c>), so this base keys the <c>Parameters</c> bag with that full name while the
    /// authored/generated property bodies stay short (<c>get =&gt; Get&lt;Guid&gt;();</c>). Pass the API unique name to
    /// the base constructor once. Standard SDK/typed messages keep using <see cref="TypedRequest"/> unchanged.
    /// </summary>
    public abstract class CustomApiRequest : TypedRequest
    {
        /// <summary>Creates the request and sets <see cref="OrganizationRequest.RequestName"/> to the API unique name.</summary>
        /// <param name="apiUniqueName">The custom API unique name (for example <c>publisher_MyApi</c>).</param>
        protected CustomApiRequest(string apiUniqueName) => RequestName = apiUniqueName;

        /// <inheritdoc />
        protected override T Get<T>([CallerMemberName] string name = "") => base.Get<T>($"{RequestName}.{name}");

        /// <inheritdoc />
        protected override void Set<T>(T value, [CallerMemberName] string name = "") => base.Set(value, $"{RequestName}.{name}");
    }

    /// <summary>
    /// Base class for a Vela-owned typed response. Each typed property is a one-liner over the underlying
    /// <c>Results</c> bag: <c>public decimal Total =&gt; Get&lt;decimal&gt;();</c> (read-only) or with a
    /// <c>set =&gt; Set(value);</c>. The result key defaults to the property name.
    /// </summary>
    public abstract class TypedResponse : OrganizationResponse
    {
        /// <summary>Reads a typed value from <c>Results</c> (defaults when absent). Key defaults to the property name.</summary>
        /// <typeparam name="T">The result's CLR type.</typeparam>
        /// <param name="name">The result key; defaults to the calling property's name.</param>
        protected virtual T Get<T>([CallerMemberName] string name = "") => Results.Contains(name) && Results[name] is T typed ? typed : default!;

        /// <summary>Writes a typed value into <c>Results</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The result's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The result key; defaults to the calling property's name.</param>
        protected virtual void Set<T>(T value, [CallerMemberName] string name = "") => Results[name] = value!;
    }

    /// <summary>
    /// Base class for a typed <b>Custom API</b> response. Dataverse names a custom API's response properties
    /// <c>{apiUniqueName}.{propName}.response</c> in the plugin's <c>OutputParameters</c> and in the returned
    /// <c>Results</c>, so this base keys the <c>Results</c> bag with that full name while the authored/generated
    /// property bodies stay short (<c>get =&gt; Get&lt;decimal&gt;();</c>). Pass the API unique name to the base
    /// constructor once. Standard/typed responses keep using <see cref="TypedResponse"/> unchanged.
    /// </summary>
    public abstract class CustomApiResponse : TypedResponse
    {
        private readonly string _apiUniqueName;

        /// <summary>Creates the response bound to the custom API whose response properties it maps.</summary>
        /// <param name="apiUniqueName">The custom API unique name (for example <c>publisher_MyApi</c>).</param>
        protected CustomApiResponse(string apiUniqueName) => _apiUniqueName = apiUniqueName;

        /// <inheritdoc />
        protected override T Get<T>([CallerMemberName] string name = "") => base.Get<T>($"{_apiUniqueName}.{name}.response");

        /// <inheritdoc />
        protected override void Set<T>(T value, [CallerMemberName] string name = "") => base.Set(value, $"{_apiUniqueName}.{name}.response");
    }

    /// <summary>A typed Create request whose <c>Target</c> is the early-bound <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public sealed class CreateRequest<TEntity> : TypedRequest where TEntity : Entity
    {
        /// <summary>Creates a Create request over the live <c>InputParameters</c>.</summary>
        public CreateRequest()
        {
            RequestName = "Create";
        }

        /// <summary>The record being created, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }
    }

    /// <summary>A typed Update request whose <c>Target</c> is the early-bound <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public sealed class UpdateRequest<TEntity> : TypedRequest where TEntity : Entity
    {
        /// <summary>Creates an Update request over the live <c>InputParameters</c>.</summary>
        public UpdateRequest()
        {
            RequestName = "Update";
        }

        /// <summary>The record being updated, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }
    }

    /// <summary>A typed SendEmailFromTemplate request whose <c>Target</c> (the email) is early-bound.</summary>
    /// <typeparam name="TEntity">The step's entity type (the email record).</typeparam>
    public sealed class SendEmailFromTemplateRequest<TEntity> : TypedRequest where TEntity : Entity
    {
        /// <summary>Creates a SendEmailFromTemplate request over the live <c>InputParameters</c>.</summary>
        public SendEmailFromTemplateRequest()
        {
            RequestName = "SendEmailFromTemplate";
        }

        /// <summary>The email record, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }

        /// <summary>The email template id.</summary>
        public System.Guid TemplateId { get => Get<System.Guid>(); set => Set(value); }

        /// <summary>The id of the record the email is regarding.</summary>
        public System.Guid RegardingId { get => Get<System.Guid>(); set => Set(value); }

        /// <summary>The entity type of the record the email is regarding.</summary>
        public string RegardingType { get => Get<string>(); set => Set(value); }
    }

    /// <summary>A typed Upsert request whose <c>Target</c> is the early-bound <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public sealed class UpsertRequest<TEntity> : TypedRequest where TEntity : Entity
    {
        /// <summary>Creates an Upsert request over the live <c>InputParameters</c>.</summary>
        public UpsertRequest()
        {
            RequestName = "Upsert";
        }

        /// <summary>The record being upserted, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }
    }

    /// <summary>The typed Upsert response: whether a record was created, and a reference to it.</summary>
    public sealed class UpsertResponse : TypedResponse
    {
        /// <summary>True when a new record was created; false when an existing record was updated.</summary>
        public bool RecordCreated { get => Get<bool>(); set => Set(value); }

        /// <summary>A reference to the created or updated record.</summary>
        public EntityReference Target { get => Get<EntityReference>(); set => Set(value); }
    }
}
