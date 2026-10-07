using System;
using Vela.Context;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata without dedicated request/response CLR models.
/// </summary>
public interface ICustomApiBuilder : ICustomApiMetadata<ICustomApiBuilder>
{
    /// <summary>
    /// Sets the instance method that should execute the Custom API.
    /// </summary>
    /// <param name="methodName">The handler method name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder Handler(string methodName);

    /// <summary>
    /// Sets the instance method that should execute the Custom API.
    /// </summary>
    /// <param name="handler">The handler delegate.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder Handler(Action<ILocalPluginContext> handler);

    /// <summary>
    /// Adds a request parameter. It is required unless <paramref name="optional"/> is true.
    /// </summary>
    /// <typeparam name="TParameter">Parameter CLR type.</typeparam>
    /// <param name="name">Parameter name: letters, digits and underscores, unique within the request.</param>
    /// <param name="optional">Whether the parameter is optional.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="logicalEntityName">The table of an Entity, EntityReference or EntityCollection parameter.
    /// Defaults to the early-bound entity's logical name when <typeparamref name="TParameter"/> is one.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null, string? logicalEntityName = null);

    /// <summary>
    /// Adds a request parameter. It is required unless <paramref name="optional"/> is true.
    /// </summary>
    /// <param name="name">Parameter name: letters, digits and underscores, unique within the request.</param>
    /// <param name="parameterType">Parameter CLR type.</param>
    /// <param name="optional">Whether the parameter is optional.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="logicalEntityName">The table of an Entity, EntityReference or EntityCollection parameter.
    /// Defaults to the early-bound entity's logical name when <paramref name="parameterType"/> is one.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null, string? logicalEntityName = null);

    /// <summary>
    /// Adds a response parameter.
    /// </summary>
    /// <typeparam name="TParameter">Parameter CLR type.</typeparam>
    /// <param name="name">Parameter name: letters, digits and underscores, unique within the response.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="logicalEntityName">The table of an Entity or EntityReference property. Defaults to the
    /// early-bound entity's logical name when <typeparamref name="TParameter"/> is one.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null, string? logicalEntityName = null);

    /// <summary>
    /// Adds a response parameter.
    /// </summary>
    /// <param name="name">Parameter name: letters, digits and underscores, unique within the response.</param>
    /// <param name="parameterType">Parameter CLR type.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="logicalEntityName">The table of an Entity or EntityReference property. Defaults to the
    /// early-bound entity's logical name when <paramref name="parameterType"/> is one.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null, string? logicalEntityName = null);

    /// <summary>
    /// Validates and builds the definition.
    /// </summary>
    /// <returns>The custom API definition.</returns>
    /// <exception cref="InvalidOperationException">The definition breaks a Dataverse Custom API rule.</exception>
    CustomApiDefinition Build();
}
