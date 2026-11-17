using System;

namespace Vela.Messages;

/// <summary>
/// Marks the request of a global Custom API (bound to no table). Dataverse gives such an API one message filter, with
/// no table (primary entity <c>"none"</c>), and its steps see <c>PrimaryEntityName</c> <c>"none"</c>. A step declared
/// from the request (<c>On&lt;TRequest&gt;()</c>, or the generated <c>On&lt;Name&gt;&lt;TEntity&gt;()</c>) therefore
/// registers with no table, whatever the plugin's <c>TEntity</c>, like a step on <c>Associate</c>. <c>vela models
/// --custom-apis</c> emits it on the request of a global API; add it to a hand-written request of a global API.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class GlobalCustomApiAttribute : Attribute
{
}
