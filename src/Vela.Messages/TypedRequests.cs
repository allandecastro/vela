using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Vela.Messages
{
    // Vela-owned typed message requests (typed-requests design). Unlike the SDK request types (sealed, Target
    // typed as the base Entity), a Vela request is generic over the entity, so ctx.Request exposes an early-bound
    // Target - the single typed accessor for the primary record. The Target getter mirrors GetTarget<T>() exactly
    // (Parameters["Target"].ToEntity<TEntity>()), so the two accessors can never disagree.

    /// <summary>
    /// Base class for a Vela-owned typed request. Each typed property is a one-liner over the underlying
    /// <c>Parameters</c> bag: <c>public Guid AccountId { get =&gt; Get&lt;Guid&gt;(); set =&gt; Set(value); }</c>.
    /// The parameter key defaults to the property name (via <see cref="CallerMemberNameAttribute"/>), so authored
    /// and generated request classes need no <c>Parameters.Contains(...)</c> boilerplate.
    /// </summary>
    public abstract class TypedRequest : OrganizationRequest
    {
        /// <summary>
        /// Reads a typed value from <c>Parameters</c> (defaults when absent or null). Key defaults to the property name.
        /// A base <c>Entity</c> is converted to an early-bound <typeparamref name="T"/>; a value of any other type
        /// throws instead of silently reading as the default.
        /// </summary>
        /// <typeparam name="T">The parameter's CLR type.</typeparam>
        /// <param name="name">The parameter key; defaults to the calling property's name.</param>
        protected virtual T Get<T>([CallerMemberName] string name = "") => TypedValue.Read<T>(Parameters.Contains(name), Parameters.Contains(name) ? Parameters[name] : null, name);

        /// <summary>Writes a typed value into <c>Parameters</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The parameter's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The parameter key; defaults to the calling property's name.</param>
        protected virtual void Set<T>(T value, [CallerMemberName] string name = "") => Parameters[name] = value!;
    }

    /// <summary>
    /// Base class for a typed <b>Custom API</b> request. A custom API's request parameters carry bare-identifier
    /// uniquenames equal to their plain names, so Dataverse exposes them - and the plugin's <c>InputParameters</c>
    /// are keyed - by that plain name. This base therefore inherits the plain-name keying of
    /// <see cref="TypedRequest"/> (the <c>Parameters</c> bag is keyed by the property name); it only sets
    /// <see cref="OrganizationRequest.RequestName"/> to the API unique name. Pass the API unique name to the base
    /// constructor once. Standard SDK/typed messages keep using <see cref="TypedRequest"/> unchanged.
    /// </summary>
    public abstract class CustomApiRequest : TypedRequest
    {
        /// <summary>Creates the request and sets <see cref="OrganizationRequest.RequestName"/> to the API unique name.</summary>
        /// <param name="apiUniqueName">The custom API unique name (for example <c>publisher_MyApi</c>).</param>
        protected CustomApiRequest(string apiUniqueName) => RequestName = apiUniqueName;
    }

    /// <summary>
    /// Base class for a Vela-owned typed response. Each typed property is a one-liner over the underlying
    /// <c>Results</c> bag: <c>public decimal Total =&gt; Get&lt;decimal&gt;();</c> (read-only) or with a
    /// <c>set =&gt; Set(value);</c>. The result key defaults to the property name.
    /// </summary>
    public abstract class TypedResponse : OrganizationResponse
    {
        /// <summary>
        /// Reads a typed value from <c>Results</c> (defaults when absent or null). Key defaults to the property name.
        /// A base <c>Entity</c> is converted to an early-bound <typeparamref name="T"/>; a value of any other type
        /// throws instead of silently reading as the default.
        /// </summary>
        /// <typeparam name="T">The result's CLR type.</typeparam>
        /// <param name="name">The result key; defaults to the calling property's name.</param>
        protected virtual T Get<T>([CallerMemberName] string name = "") => TypedValue.Read<T>(Results.Contains(name), Results.Contains(name) ? Results[name] : null, name);

        /// <summary>Writes a typed value into <c>Results</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The result's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The result key; defaults to the calling property's name.</param>
        protected virtual void Set<T>(T value, [CallerMemberName] string name = "") => Results[name] = value!;
    }

    /// <summary>
    /// Base class for a typed <b>Custom API</b> response. A custom API's response properties carry bare-identifier
    /// uniquenames equal to their plain names, so the plugin's <c>OutputParameters</c> and the returned
    /// <c>Results</c> are keyed by that plain name. This base therefore inherits the plain-name keying of
    /// <see cref="TypedResponse"/> (the <c>Results</c> bag is keyed by the property name). Pass the API unique name
    /// to the base constructor once. Standard/typed responses keep using <see cref="TypedResponse"/> unchanged.
    /// </summary>
    public abstract class CustomApiResponse : TypedResponse
    {
        /// <summary>
        /// Creates the response bound to the custom API whose response properties it maps, and sets
        /// <see cref="OrganizationResponse.ResponseName"/> to the API unique name (mirroring
        /// <see cref="CustomApiRequest"/>'s <see cref="OrganizationRequest.RequestName"/>).
        /// </summary>
        /// <param name="apiUniqueName">The custom API unique name (for example <c>publisher_MyApi</c>).</param>
        protected CustomApiResponse(string apiUniqueName) => ResponseName = apiUniqueName;
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

    // The shared read behind TypedRequest.Get / TypedResponse.Get.
    internal static class TypedValue
    {
        private static readonly System.Reflection.MethodInfo ToEntity = typeof(Entity).GetMethod(nameof(Entity.ToEntity))!;

        internal static T Read<T>(bool present, object? value, string name)
        {
            if (!present || value is null)
            {
                return default!;
            }

            if (value is T typed)
            {
                return typed;
            }

            // Dataverse hands entities over as the base Entity: convert to the declared early-bound type.
            if (value is Entity entity && typeof(Entity).IsAssignableFrom(typeof(T)))
            {
                return (T)ToEntity.MakeGenericMethod(typeof(T)).Invoke(entity, null)!;
            }

            throw new System.InvalidCastException(
                $"Parameter '{name}' holds a {value.GetType().FullName}, not the {typeof(T).FullName} its property declares. Declare the property with the type Dataverse sends.");
        }
    }
}
