using System;

namespace Vela.Messages;

/// <summary>
/// Declares the table of a Custom API model property, registered as the parameter's <c>logicalentityname</c>.
/// It applies to Entity and EntityReference properties of a request or response model, and to EntityCollection
/// properties of a request model only: Dataverse does not allow a table on an EntityCollection response property.
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
