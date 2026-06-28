using System;
using Vela.Context;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata without dedicated request/response CLR models.
/// </summary>
public interface ICustomApiBuilder
{
    /// <summary>
    /// Sets the unique name.
    /// </summary>
    /// <param name="uniqueName">The unique name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder UniqueName(string uniqueName);

    /// <summary>
    /// Sets the display name.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder DisplayName(string displayName);

    /// <summary>
    /// Sets the description.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder Description(string description);

    /// <summary>
    /// Marks the API as a function.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder AsFunction();

    /// <summary>
    /// Marks the API as an action.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder AsAction();

    /// <summary>
    /// Marks the API as bound to the specified entity.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    ICustomApiBuilder BoundTo<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to the specified entity logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder BoundTo(string entityLogicalName);

    /// <summary>
    /// Marks the API as bound to an entity collection.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    ICustomApiBuilder BoundToCollection<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to an entity collection logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder BoundToCollection(string entityLogicalName);

    /// <summary>
    /// Sets the execution privilege name.
    /// </summary>
    /// <param name="privilegeName">The privilege name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder ExecutePrivilege(string privilegeName);

    /// <summary>
    /// Marks the API as private.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder Private();

    /// <summary>
    /// Marks the API as public.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder Public();

    /// <summary>
    /// Sets allowed custom processing step type.
    /// </summary>
    /// <param name="stepType">Allowed step type.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType);

    /// <summary>
    /// Enables or disables workflow SDK step support.
    /// </summary>
    /// <param name="enabled">True to enable workflow SDK step support.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder WorkflowSdkStep(bool enabled);

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
    /// Adds a request parameter.
    /// </summary>
    /// <typeparam name="TParameter">Parameter CLR type.</typeparam>
    /// <param name="name">Parameter name.</param>
    /// <param name="optional">Whether the parameter is optional.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null);

    /// <summary>
    /// Adds a request parameter.
    /// </summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="parameterType">Parameter CLR type.</param>
    /// <param name="optional">Whether the parameter is optional.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null);

    /// <summary>
    /// Adds a response parameter.
    /// </summary>
    /// <typeparam name="TParameter">Parameter CLR type.</typeparam>
    /// <param name="name">Parameter name.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null);

    /// <summary>
    /// Adds a response parameter.
    /// </summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="parameterType">Parameter CLR type.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null);

    /// <summary>
    /// Builds the definition.
    /// </summary>
    /// <returns>The custom API definition.</returns>
    CustomApiDefinition Build();
}
