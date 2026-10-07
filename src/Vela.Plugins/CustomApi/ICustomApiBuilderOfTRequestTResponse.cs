using System;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata.
/// </summary>
/// <typeparam name="TRequest">The request model type.</typeparam>
/// <typeparam name="TResponse">The response model type.</typeparam>
public interface ICustomApiBuilder<TRequest, TResponse> : ICustomApiMetadata<ICustomApiBuilder<TRequest, TResponse>>
    where TRequest : OrganizationRequest, new()
    where TResponse : OrganizationResponse, new()
{
    /// <summary>
    /// Configures request parameters. Without a call, every settable property of <typeparamref name="TRequest"/>
    /// becomes a parameter, optional unless it carries <c>[Required]</c>.
    /// </summary>
    /// <param name="configure">The request parameter configuration.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Request(Action<ICustomApiParameterBuilder<TRequest>> configure);

    /// <summary>
    /// Configures response parameters. Without a call, every property of <typeparamref name="TResponse"/> becomes
    /// a response property.
    /// </summary>
    /// <param name="configure">The response parameter configuration.</param>
    /// <returns>The builder.</returns>
    ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure);

    /// <summary>
    /// Validates and builds the definition.
    /// </summary>
    /// <returns>The custom API definition.</returns>
    /// <exception cref="InvalidOperationException">The definition breaks a Dataverse Custom API rule.</exception>
    CustomApiDefinition Build();
}
