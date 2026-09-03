using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;

namespace Vela.StepBuilder;

/// <summary>
/// Forces filtering attributes on Create / Update steps: the step cannot be configured further until at
/// least one filtering attribute is specified. There is deliberately no "all attributes" escape - an
/// unfiltered Create/Update step fires on every change, which is almost always a mistake (to fire on
/// everything, be explicit and list the attributes). Once filtering is set, the capability terminal
/// <typeparamref name="TNext"/> (images, settings, build) is returned.
/// </summary>
/// <typeparam name="TEntity">The entity type for this step.</typeparam>
/// <typeparam name="TNext">The capability terminal reached after filtering is set.</typeparam>
public interface IFilteringSelector<TEntity, out TNext> where TEntity : Entity
{
    /// <summary>
    /// Configure the filtering attributes that trigger this step. At least one attribute is required
    /// (the first parameter is mandatory) - the step fires only when one of these columns is in the
    /// request payload.
    /// </summary>
    /// <param name="attribute">The first (required) filtering attribute.</param>
    /// <param name="additionalAttributes">Any further filtering attributes.</param>
    TNext FilteringAttributes(
        Expression<Func<TEntity, object?>> attribute,
        params Expression<Func<TEntity, object?>>[] additionalAttributes);
}
