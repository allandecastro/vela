using System;
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
    private static readonly Assembly[] SdkAssemblies =
    [
        typeof(CreateRequest).Assembly,
        typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly,
    ];

    public static bool HasSdkRequestClass(string messageName) => TryGetSdkRequestTypeName(messageName, out _);

    /// <summary>
    /// The fully-qualified SDK request type for the request <paramref name="messageName"/> (case-insensitive), with
    /// the SDK's own casing: <c>Microsoft.Xrm.Sdk.Messages.CreateRequest</c>,
    /// <c>Microsoft.Crm.Sdk.Messages.CloseQuoteRequest</c>.
    /// </summary>
    public static bool TryGetSdkRequestTypeName(string messageName, out string typeName)
    {
        typeName = string.Empty;
        if (string.IsNullOrWhiteSpace(messageName))
        {
            return false;
        }

        foreach ((Assembly assembly, string ns) in new[] { (SdkAssemblies[0], "Microsoft.Xrm.Sdk.Messages"), (SdkAssemblies[1], "Microsoft.Crm.Sdk.Messages") })
        {
            Type? type = assembly.GetType($"{ns}.{messageName.Trim()}Request", throwOnError: false, ignoreCase: true);
            if (type != null && typeof(Microsoft.Xrm.Sdk.OrganizationRequest).IsAssignableFrom(type))
            {
                typeName = type.FullName!;
                return true;
            }
        }

        return false;
    }
}
