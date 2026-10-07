namespace Vela.CustomApi;

/// <summary>
/// Represents a single Custom API request or response parameter.
/// </summary>
public sealed class CustomApiParameterDefinition
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CLR type name for the parameter value.
    /// </summary>
    public string ParameterTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the parameter is optional.
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the parameter is a collection.
    /// </summary>
    public bool IsCollection { get; set; }

    /// <summary>
    /// Gets or sets the table of an Entity, EntityReference or EntityCollection parameter (its
    /// <c>logicalentityname</c>), or null when the parameter is not tied to a table.
    /// </summary>
    public string? LogicalEntityName { get; set; }

    /// <summary>
    /// Gets or sets the parameter description.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
