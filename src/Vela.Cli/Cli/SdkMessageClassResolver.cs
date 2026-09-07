using System;
using Microsoft.Xrm.Sdk.Messages;

namespace Vela.Cli;

/// <summary>
/// Detects whether an SDK message already has a strongly-typed request class shipped by the Dataverse SDK, so the
/// message generator can skip it (generating a wrapper would only duplicate the SDK type and risk a CS0104 name
/// collision). Detection is by reflection over the loaded <c>Microsoft.Xrm.Sdk</c> assembly: a message
/// <c>&lt;Name&gt;</c> is considered SDK-backed when <c>Microsoft.Xrm.Sdk.Messages.&lt;Name&gt;Request</c> exists.
/// </summary>
/// <remarks>
/// KNOWN LIMITATION (follow-up): only <c>Microsoft.Xrm.Sdk.Messages.*</c> is probed. The Sales/business messages
/// in <c>Microsoft.Crm.Sdk.Messages.*</c> (e.g. <c>QualifyLeadRequest</c>, <c>WinOpportunityRequest</c>) are NOT
/// detected, because <c>Vela.Cli</c> deliberately does not reference <c>Microsoft.CrmSdk.CoreAssemblies</c> and so
/// that assembly is not loaded. Such messages may therefore still be generated in ALL mode; the resulting wrapper
/// is a harmless duplicate and the developer can prefer the real SDK type via <c>OnMessage&lt;XxxRequest&gt;()</c>.
/// </remarks>
internal static class SdkMessageClassResolver
{
    // The assembly that carries the core SDK request types (CreateRequest, UpdateRequest, ...). Resolved from a
    // known type rather than by name so it tracks whatever Microsoft.Xrm.Sdk version Vela.Cli references.
    private static readonly System.Reflection.Assembly SdkAssembly = typeof(CreateRequest).Assembly;

    /// <summary>
    /// True when the Dataverse SDK already ships a <c>&lt;messageName&gt;Request</c> type in
    /// <c>Microsoft.Xrm.Sdk.Messages</c> (e.g. <c>"Create"</c> → <c>CreateRequest</c>). See the remarks on this
    /// class for why <c>Microsoft.Crm.Sdk.Messages.*</c> types are not detected.
    /// </summary>
    public static bool HasSdkRequestClass(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            return false;
        }

        return SdkAssembly.GetType($"Microsoft.Xrm.Sdk.Messages.{messageName}Request") != null;
    }
}
