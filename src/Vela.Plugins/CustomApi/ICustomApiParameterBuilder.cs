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
    /// Adds a required parameter.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property selector.</param>
    /// <returns>The builder.</returns>
    ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property);

    /// <summary>
    /// Adds a parameter and marks it optional when requested.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property selector.</param>
    /// <param name="optional">Whether the parameter is optional.</param>
    /// <param name="description">Optional parameter description.</param>
    /// <returns>The builder.</returns>
    ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property, bool optional, string? description = null);
}
