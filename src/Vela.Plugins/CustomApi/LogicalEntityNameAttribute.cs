using System;

namespace Vela.CustomApi;

/// <summary>
/// Declares the table of an Entity, EntityReference or EntityCollection property of a Custom API request or
/// response model. It is registered as the parameter's <c>logicalentityname</c>.
/// </summary>
/// <remarks>
/// An early-bound entity property (such as <c>Account</c>) needs no attribute: its table comes from the type.
/// </remarks>
/// <param name="logicalName">The table's logical name, for example <c>account</c>.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class LogicalEntityNameAttribute(string logicalName) : Attribute
{
    /// <summary>
    /// Gets the table's logical name.
    /// </summary>
    public string LogicalName { get; } = logicalName;
}
