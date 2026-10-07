using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi;

/// <summary>
/// Fluent builder for Custom API metadata.
/// </summary>
/// <typeparam name="TRequest">The request model type.</typeparam>
/// <typeparam name="TResponse">The response model type.</typeparam>
public sealed class CustomApiBuilder<TRequest, TResponse> : CustomApiBuilderBase<ICustomApiBuilder<TRequest, TResponse>>, ICustomApiBuilder<TRequest, TResponse>
    where TRequest : OrganizationRequest, new()
    where TResponse : OrganizationResponse, new()
{
    // Members inherited from OrganizationRequest/OrganizationResponse are infrastructure, never Custom API
    // parameters. DeclaredOnly already excludes them; this set is a defensive backstop should a generated
    // type re-declare one of them.
    private static readonly HashSet<string> BaseMemberNames = new(StringComparer.Ordinal)
    {
        nameof(OrganizationRequest.RequestId),
        nameof(OrganizationRequest.RequestName),
        nameof(OrganizationRequest.Parameters),
        nameof(OrganizationResponse.ResponseName),
        nameof(OrganizationResponse.Results),
        "ExtensionData",
        "Item",
    };

    private bool _requestConfigured;
    private bool _responseConfigured;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomApiBuilder{TRequest, TResponse}"/> class.
    /// </summary>
    public CustomApiBuilder()
    {
        Definition.RequestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;
        Definition.ResponseTypeName = typeof(TResponse).FullName ?? typeof(TResponse).Name;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Request(Action<ICustomApiParameterBuilder<TRequest>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _requestConfigured = true;
        // Accumulate across calls: a second .Request(...) must add to the parameters, not replace them.
        configure(new ParameterBuilder<TRequest>(RequestParameters, isOutput: false));
        return this;
    }

    /// <inheritdoc />
    public ICustomApiBuilder<TRequest, TResponse> Response(Action<ICustomApiParameterBuilder<TResponse>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _responseConfigured = true;
        // Accumulate across calls (see Request): a second .Response(...) adds to the properties, not replaces them.
        configure(new ParameterBuilder<TResponse>(ResponseParameters, isOutput: true));
        return this;
    }

    /// <inheritdoc />
    public CustomApiDefinition Build()
        => Complete(
            _requestConfigured ? RequestParameters.ToArray() : InferParameters(typeof(TRequest), isOutput: false),
            _responseConfigured ? ResponseParameters.ToArray() : InferParameters(typeof(TResponse), isOutput: true));

    // Without .Request(...)/.Response(...), every own property of the model is a parameter. An inferred request
    // parameter is optional unless the property carries [Required].
    private static CustomApiParameterDefinition[] InferParameters(Type modelType, bool isOutput)
    {
        List<CustomApiParameterDefinition> parameters = new List<CustomApiParameterDefinition>();

        // DeclaredOnly reflects only the request/response's own typed properties, never the inherited
        // OrganizationRequest/OrganizationResponse members (RequestName, Parameters, Results, the indexer, ...).
        foreach (PropertyInfo property in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetIndexParameters().Length > 0 || BaseMemberNames.Contains(property.Name))
            {
                continue;
            }

            if (!property.CanRead || (!isOutput && !property.CanWrite))
            {
                continue;
            }

            // Through AddParameter, so two properties differing only by case (Foo, foo) fail like two Add calls.
            AddParameter(parameters, FromProperty(property, isOutput, optional: property.GetCustomAttribute<RequiredAttribute>() == null, description: null, logicalEntityName: null));
        }

        return parameters.ToArray();
    }

    private static CustomApiParameterDefinition FromProperty(PropertyInfo property, bool isOutput, bool optional, string? description, string? logicalEntityName)
        => CreateParameter(
            property.Name,
            property.PropertyType,
            isOutput,
            optional,
            description ?? property.GetCustomAttribute<DescriptionAttribute>()?.Description,
            logicalEntityName ?? property.GetCustomAttribute<Vela.Messages.LogicalEntityNameAttribute>()?.LogicalName);

    private sealed class ParameterBuilder<TModel>(ICollection<CustomApiParameterDefinition> parameters, bool isOutput) : ICustomApiParameterBuilder<TModel>
        where TModel : class
    {
        public ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property)
            => Add(property, optional: false, description: null);

        public ICustomApiParameterBuilder<TModel> Add<TProperty>(Expression<Func<TModel, TProperty>> property, bool optional, string? description = null, string? logicalEntityName = null)
        {
            property = property ?? throw new ArgumentNullException(nameof(property));

            if (GetMemberExpression(property)?.Member is not PropertyInfo propertyInfo)
            {
                throw new ArgumentException("Expression must target a property.", nameof(property));
            }

            // An explicitly added request parameter is required unless optional: true. [Required] only matters for
            // the parameters inferred from the model when .Request(...) is not called.
            AddParameter(parameters, FromProperty(propertyInfo, isOutput, optional, description, logicalEntityName));
            return this;
        }

        private static MemberExpression? GetMemberExpression<TProperty>(Expression<Func<TModel, TProperty>> property)
        {
            Expression body = property.Body;
            if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            return body as MemberExpression;
        }
    }
}
