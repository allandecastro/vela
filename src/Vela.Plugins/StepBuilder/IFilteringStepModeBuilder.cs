using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Forces filtering attributes on Create / Update steps: the step cannot be built until at least one
/// filtering attribute is specified. There is deliberately no "all attributes" escape — an unfiltered
/// Create/Update step fires on every change, which is almost always a mistake (to fire on everything,
/// be explicit and list the attributes). Once filtering is set, the normal
/// <see cref="IStepModeBuilder{TEntity}"/> surface (images, order, handler, build) is returned.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
public interface IFilteringStepModeBuilder<TEntity> where TEntity : Entity
{
    /// <summary>
    /// Configure the filtering attributes that trigger this step. At least one attribute is required
    /// (the first parameter is mandatory) — the step fires only when one of these columns is in the
    /// request payload.
    /// </summary>
    /// <param name="attribute">The first (required) filtering attribute.</param>
    /// <param name="additionalAttributes">Any further filtering attributes.</param>
    IStepModeBuilder<TEntity> FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute,
        params Expression<Func<TEntity, object?>>[] additionalAttributes);
}
