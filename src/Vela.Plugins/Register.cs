using Vela.CustomApi;
using Vela.StepBuilder;
using Microsoft.Xrm.Sdk;

namespace Vela;

/// <summary>
/// Root entry point for framework registration DSLs.
/// </summary>
public static class Register
{
    /// <summary>
    /// Starts configuring a plugin step for an early-bound entity.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>A fluent step builder.</returns>
    public static IStepBuilder<TEntity> RegisterPluginStep<TEntity>()
        where TEntity : Entity
    {
        return new StepBuilder<TEntity>();
    }

    /// <summary>
    /// Starts configuring a Custom API definition.
    /// </summary>
    /// <typeparam name="TRequest">The request model type.</typeparam>
    /// <typeparam name="TResponse">The response model type.</typeparam>
    /// <returns>A fluent Custom API builder.</returns>
    public static ICustomApiBuilder<TRequest, TResponse> RegisterCustomApi<TRequest, TResponse>()
        where TRequest : OrganizationRequest, new()
        where TResponse : OrganizationResponse, new()
    {
        return new CustomApiBuilder<TRequest, TResponse>();
    }

    /// <summary>
    /// Starts configuring a lightweight Custom API definition without dedicated request/response CLR models.
    /// </summary>
    /// <returns>A fluent lightweight Custom API builder.</returns>
    public static ICustomApiBuilder RegisterCustomApi()
    {
        return new CustomApiLightBuilder();
    }
}
