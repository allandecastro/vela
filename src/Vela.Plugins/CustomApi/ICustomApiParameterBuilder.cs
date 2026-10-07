using System;
using System.Linq.Expressions;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API request or response parameters.
/// </summary>
/// <typeparam name="TModel">The request or response model type.</typeparam>
public interface ICustomApiParameterBuilder<TModel>
    where TModel : class
{
    /// <summary>
    /// Adds a required parameter (a response property has no optionality).
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property selector.</param>
    /// <returns>The builder.</returns>
    ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property);

    /// <summary>
    /// Adds a parameter, required unless <paramref name="optional"/> is true. <paramref name="optional"/> is
    /// ignored for a response property, which has no optionality in Dataverse.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property selector.</param>
    /// <param name="optional">Whether the request parameter is optional.</param>
    /// <param name="description">Optional parameter description; defaults to the property's <c>[Description]</c>.</param>
    /// <param name="logicalEntityName">The table of an Entity, EntityReference or EntityCollection parameter;
    /// defaults to the property's <see cref="LogicalEntityNameAttribute"/>, then to the early-bound entity's
    /// logical name.</param>
    /// <returns>The builder.</returns>
    ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property, bool optional, string? description = null, string? logicalEntityName = null);
}
