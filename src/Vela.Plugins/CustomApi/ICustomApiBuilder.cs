using System;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata.
/// </summary>
/// <typeparam name="TRequest">The request model type.</typeparam>
/// <typeparam name="TResponse">The response model type.</typeparam>
public interface ICustomApiBuilder<TRequest, TResponse>
    where TRequest : class, new()
    where TResponse : class, new()
{
    /// <summary>
    /// Sets the unique name.
    /// </summary>
    /// <param name="uniqueName">The unique name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> UniqueName(string uniqueName);

    /// <summary>
    /// Sets the display name.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> DisplayName(string displayName);

    /// <summary>
    /// Sets the description.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Description(string description);

    /// <summary>
    /// Marks the API as a function.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> AsFunction();

    /// <summary>
    /// Marks the API as an action.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> AsAction();

    /// <summary>
    /// Marks the API as bound to the specified entity.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> BoundTo<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to an entity logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> BoundTo(string entityLogicalName);

    /// <summary>
    /// Marks the API as bound to an entity collection.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> BoundToCollection<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to an entity collection logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> BoundToCollection(string entityLogicalName);

    /// <summary>
    /// Sets the execution privilege name.
    /// </summary>
    /// <param name="privilegeName">The privilege name.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> ExecutePrivilege(string privilegeName);

    /// <summary>
    /// Marks the API as private.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Private();

    /// <summary>
    /// Marks the API as public.
    /// </summary>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Public();

    /// <summary>
    /// Sets allowed custom processing step type.
    /// </summary>
    /// <param name="stepType">Allowed step type.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType);

    /// <summary>
    /// Enables or disables workflow SDK step support.
    /// </summary>
    /// <param name="enabled">True to enable workflow SDK step support.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> WorkflowSdkStep(bool enabled);

    /// <summary>
    /// Configures request parameters.
    /// </summary>
    /// <param name="configure">The request parameter configuration.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Request(Action<ICustomApiParameterBuilder<TRequest>> configure);

    /// <summary>
    /// Configures response parameters.
    /// </summary>
    /// <param name="configure">The response parameter configuration.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure);

    /// <summary>
    /// Builds the definition.
    /// </summary>
    /// <returns>The custom API definition.</returns>
    CustomApiDefinition Build();
}
