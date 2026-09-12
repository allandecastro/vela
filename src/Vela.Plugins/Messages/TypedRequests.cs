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
        protected T Get<T>([CallerMemberName] string name = "") => Parameters.Contains(name) ? (T)Parameters[name] : default!;

        /// <summary>Writes a typed value into <c>Parameters</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The parameter's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The parameter key; defaults to the calling property's name.</param>
        protected void Set<T>(T value, [CallerMemberName] string name = "") => Parameters[name] = value!;
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
        protected T Get<T>([CallerMemberName] string name = "") => Results.Contains(name) ? (T)Results[name] : default!;

        /// <summary>Writes a typed value into <c>Results</c>. Key defaults to the property name.</summary>
        /// <typeparam name="T">The result's CLR type.</typeparam>
        /// <param name="value">The value to store.</param>
        /// <param name="name">The result key; defaults to the calling property's name.</param>
        protected void Set<T>(T value, [CallerMemberName] string name = "") => Results[name] = value!;
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
}
