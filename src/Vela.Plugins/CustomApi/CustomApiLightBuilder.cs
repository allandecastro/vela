using System;
using Vela.Context;

namespace Vela.CustomApi;

/// <summary>
/// Lightweight builder for Custom API metadata without dedicated CLR request/response models.
/// </summary>
public sealed class CustomApiLightBuilder : CustomApiBuilderBase<ICustomApiBuilder>, ICustomApiBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CustomApiLightBuilder"/> class.
    /// </summary>
    public CustomApiLightBuilder()
    {
    }

    /// <inheritdoc />
    public ICustomApiBuilder Handler(string methodName)
    {
        Definition.HandlerMethodName = RequireValue(methodName, nameof(methodName));
        Definition.RuntimeHandler = null;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder Handler(Action<ILocalPluginContext> handler)
    {
        handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (handler.Method.IsStatic)
        {
            throw new ArgumentException("Handler delegate must reference an instance method.", nameof(handler));
        }

        // Keep the delegate: the name alone resolves only for a method of the plug-in type (or a lambda capturing
        // `this`), not for a lambda compiled onto a compiler-generated class or a method of another object.
        Definition.HandlerMethodName = handler.Method.Name;
        Definition.RuntimeHandler = handler;
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder RequestParameter<TParameter>(string name, bool optional = false, string? description = null, string? logicalEntityName = null)
        => RequestParameter(name, typeof(TParameter), optional, description, logicalEntityName);

    /// <inheritdoc />
    public ICustomApiBuilder RequestParameter(string name, Type parameterType, bool optional = false, string? description = null, string? logicalEntityName = null)
    {
        AddParameter(RequestParameters, CreateParameter(name, parameterType, isOutput: false, optional, description, logicalEntityName));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder ResponseParameter<TParameter>(string name, string? description = null, string? logicalEntityName = null)
        => ResponseParameter(name, typeof(TParameter), description, logicalEntityName);

    /// <inheritdoc />
    public ICustomApiBuilder ResponseParameter(string name, Type parameterType, string? description = null, string? logicalEntityName = null)
    {
        AddParameter(ResponseParameters, CreateParameter(name, parameterType, isOutput: true, optional: false, description, logicalEntityName));
        return this;
    }

    /// <inheritdoc />
    public CustomApiDefinition Build() => Complete(RequestParameters.ToArray(), ResponseParameters.ToArray());
}
