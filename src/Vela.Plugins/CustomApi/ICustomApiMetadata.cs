using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi;

/// <summary>
/// The Custom API metadata shared by every Custom API builder: names, function or action, binding, privilege,
/// visibility and processing-step options.
/// </summary>
/// <typeparam name="TSelf">The builder type each method returns, so a fluent chain keeps the builder's own members.</typeparam>
public interface ICustomApiMetadata<out TSelf>
{
    /// <summary>
    /// Sets the unique name.
    /// </summary>
    /// <param name="uniqueName">The unique name.</param>
    /// <returns>The builder.</returns>
    TSelf UniqueName(string uniqueName);

    /// <summary>
    /// Sets the display name.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The builder.</returns>
    TSelf DisplayName(string displayName);

    /// <summary>
    /// Sets the description.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <returns>The builder.</returns>
    TSelf Description(string description);

    /// <summary>
    /// Marks the API as a function. A function must declare at least one response property.
    /// </summary>
    /// <returns>The builder.</returns>
    TSelf AsFunction();

    /// <summary>
    /// Marks the API as an action.
    /// </summary>
    /// <returns>The builder.</returns>
    TSelf AsAction();

    /// <summary>
    /// Marks the API as bound to the specified entity.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    TSelf BoundTo<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to an entity logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    TSelf BoundTo(string entityLogicalName);

    /// <summary>
    /// Marks the API as bound to an entity collection.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The builder.</returns>
    TSelf BoundToCollection<TEntity>() where TEntity : Entity;

    /// <summary>
    /// Marks the API as bound to an entity collection logical name.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The builder.</returns>
    TSelf BoundToCollection(string entityLogicalName);

    /// <summary>
    /// Sets the execution privilege name.
    /// </summary>
    /// <param name="privilegeName">The privilege name.</param>
    /// <returns>The builder.</returns>
    TSelf ExecutePrivilege(string privilegeName);

    /// <summary>
    /// Marks the API as private.
    /// </summary>
    /// <returns>The builder.</returns>
    TSelf Private();

    /// <summary>
    /// Marks the API as public.
    /// </summary>
    /// <returns>The builder.</returns>
    TSelf Public();

    /// <summary>
    /// Sets allowed custom processing step type.
    /// </summary>
    /// <param name="stepType">Allowed step type.</param>
    /// <returns>The builder.</returns>
    TSelf AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType stepType);

    /// <summary>
    /// Enables or disables workflow SDK step support. A workflow-enabled API must be an action, cannot use
    /// Entity, EntityCollection or StringArray parameters, and can use an EntityReference parameter only when it is
    /// bound to a table (<see cref="BoundTo{TEntity}"/> or <see cref="BoundTo(string)"/>).
    /// </summary>
    /// <param name="enabled">True to enable workflow SDK step support.</param>
    /// <returns>The builder.</returns>
    TSelf WorkflowSdkStep(bool enabled);
}
