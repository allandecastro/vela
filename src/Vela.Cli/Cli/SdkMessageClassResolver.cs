using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk.Messages;

namespace Vela.Cli;

/// <summary>
/// Detects whether an SDK request already ships a strongly-typed class in the Dataverse SDK, so the message
/// generator does not duplicate it (a duplicate risks a CS0104 name collision) and can instead point the
/// <c>On&lt;Message&gt;()</c> extension at the SDK type. Detection is by reflection over the two SDK message
/// assemblies the CLI already loads through <c>Microsoft.PowerPlatform.Dataverse.Client</c>: <c>Microsoft.Xrm.Sdk</c>
/// (<c>Microsoft.Xrm.Sdk.Messages.CreateRequest</c>, ...) and <c>Microsoft.Crm.Sdk.Proxy</c>
/// (<c>Microsoft.Crm.Sdk.Messages.QualifyLeadRequest</c>, ...). The generated project compiles against the same two
/// namespaces (<c>Microsoft.CrmSdk.CoreAssemblies</c> on net462, the Dataverse client on net10.0).
/// </summary>
internal static class SdkMessageClassResolver
{
    private static readonly (Assembly Assembly, string Namespace)[] SdkMessageNamespaces =
    [
        (typeof(CreateRequest).Assembly, "Microsoft.Xrm.Sdk.Messages"),
        (typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly, "Microsoft.Crm.Sdk.Messages"),
    ];

    /// <summary>
    /// True when the Dataverse SDK already ships a request type for <paramref name="messageName"/>: see
    /// <see cref="SdkRequestName"/>.
    /// </summary>
    public static bool HasSdkRequestClass(string messageName) => SdkRequestName(messageName) != null;

    /// <summary>
    /// The name of the SDK request that runs as <paramref name="messageName"/> and ships a <c>&lt;name&gt;Request</c>
    /// type, or null when there is none. The message is a request name or an <c>sdkmessage</c> name, which is not
    /// always the request name: <c>Send</c> is run by <c>SendEmailRequest</c>, <c>Win</c> by
    /// <c>WinOpportunityRequest</c> and <c>WinQuoteRequest</c> (see <see cref="Vela.Steps.SdkMessageNames"/>).
    /// </summary>
    public static string? SdkRequestName(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            return null;
        }

        foreach (string requestName in new[] { messageName.Trim() }.Concat(Vela.Steps.SdkMessageNames.RequestNamesFor(messageName.Trim())))
        {
            if (TryGetSdkRequestTypeName(requestName, out string typeName))
            {
                return typeName.Substring(typeName.LastIndexOf('.') + 1, typeName.Length - typeName.LastIndexOf('.') - 1 - "Request".Length);
            }
        }

        return null;
    }

    /// <summary>
    /// The fully-qualified SDK request type for the request <paramref name="requestName"/> (case-insensitive), with
    /// the SDK's own casing: <c>Microsoft.Xrm.Sdk.Messages.CreateRequest</c>,
    /// <c>Microsoft.Crm.Sdk.Messages.CloseQuoteRequest</c>.
    /// </summary>
    public static bool TryGetSdkRequestTypeName(string requestName, out string typeName)
    {
        typeName = string.Empty;
        if (string.IsNullOrWhiteSpace(requestName))
        {
            return false;
        }

        foreach ((Assembly assembly, string ns) in SdkMessageNamespaces)
        {
            Type? type = assembly.GetType($"{ns}.{requestName.Trim()}Request", throwOnError: false, ignoreCase: true);
            if (type != null && typeof(Microsoft.Xrm.Sdk.OrganizationRequest).IsAssignableFrom(type))
            {
                typeName = type.FullName!;
                return true;
            }
        }

        return false;
    }
}
