using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Messages;

namespace Vela.Cli;

/// <summary>
/// Detects whether an SDK message already has a strongly-typed request class shipped by the Dataverse SDK, so the
/// message generator can skip it (generating a wrapper would only duplicate the SDK type and risk a CS0104 name
/// collision). Detection is by reflection over the loaded <c>Microsoft.Xrm.Sdk</c> assembly (a message
/// <c>&lt;Name&gt;</c> is SDK-backed when <c>Microsoft.Xrm.Sdk.Messages.&lt;Name&gt;Request</c> exists), plus a
/// curated supplement for the Sales/business messages that live in <c>Microsoft.Crm.Sdk.Messages.*</c>.
/// </summary>
/// <remarks>
/// The Sales/business messages in <c>Microsoft.Crm.Sdk.Messages.*</c> (e.g. <c>QualifyLeadRequest</c>,
/// <c>WinOpportunityRequest</c>) cannot be found by reflection, because <c>Vela.Cli</c> deliberately does not
/// reference <c>Microsoft.CrmSdk.CoreAssemblies</c> and so that assembly is never loaded. To still skip them in
/// ALL mode we keep the curated <see cref="CrmSdkRequestMessages"/> supplement below. It is a pragmatic,
/// <b>non-exhaustive</b> list (the union of the Crm.Sdk-backed messages Vela already curates and the backlog's
/// candidate typed messages, all verified to ship a <c>*Request</c> class in <c>Microsoft.Crm.Sdk.Proxy</c>); a
/// Crm.Sdk message not listed here simply generates a harmless duplicate wrapper, which the developer can ignore
/// in favour of the real SDK type via <c>OnMessage&lt;XxxRequest&gt;()</c>.
/// </remarks>
internal static class SdkMessageClassResolver
{
    // The assembly that carries the core SDK request types (CreateRequest, UpdateRequest, ...). Resolved from a
    // known type rather than by name so it tracks whatever Microsoft.Xrm.Sdk version Vela.Cli references.
    private static readonly System.Reflection.Assembly SdkAssembly = typeof(CreateRequest).Assembly;

    // Curated supplement of well-known Microsoft.Crm.Sdk.Messages business messages that each ship a *Request class
    // in Microsoft.Crm.Sdk.Proxy. Reflection can't see these (the CrmSdk assembly isn't referenced — see remarks),
    // so listing them here lets ALL mode skip them instead of emitting a duplicate wrapper. Non-exhaustive by design.
    private static readonly HashSet<string> CrmSdkRequestMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        // Crm.Sdk-backed messages Vela already curates (On<Message>() surface).
        "QualifyLead", "WinOpportunity", "LoseOpportunity",
        "ConvertQuoteToSalesOrder", "ConvertSalesOrderToInvoice",
        "GenerateQuoteFromOpportunity", "GenerateSalesOrderFromOpportunity", "GenerateInvoiceFromOpportunity",
        "AddToQueue", "RemoveFromQueue", "PickFromQueue", "RouteTo", "ReleaseToQueue",
        "SendEmail", "SendEmailFromTemplate", "DeliverIncomingEmail", "DeliverPromoteEmail",
        "AddMembersTeam", "RemoveMembersTeam",
        "AddListMembersList", "AddMemberList", "RemoveMemberList",
        "CloseIncident", "CancelContract", "CloneContract",
        "GrantAccess", "ModifyAccess", "RevokeAccess", "Merge",
        "RetrievePrincipalAccess", "RetrieveSharedPrincipalsAndAccess", "WhoAmI",
        "WinQuote", "ReviseQuote", "FulfillSalesOrder",
        "GetQuoteProductsFromOpportunity", "GetSalesOrderProductsFromOpportunity", "GetInvoiceProductsFromOpportunity",

        // Backlog "candidate typed messages" — all Microsoft.Crm.Sdk.Messages.*Request (see BACKLOG.md P3).
        "RenewContract", "RenewEntitlement",
        "QualifyMemberList", "AddItemCampaign", "RemoveItemCampaign", "CopyCampaignResponse",
        "ExecuteWorkflow", "SetProcess", "Recalculate",
        "LockSalesOrderPricing", "UnlockSalesOrderPricing", "LockInvoicePricing", "UnlockInvoicePricing",
        "Reschedule", "ImmediateBook",
        "SendTemplate", "SendBulkMail", "SendFax",
        "ReassignObjectsOwner", "InitializeFrom",
        "AddProductToKit", "RemoveProductFromKit", "ConvertProductToKit", "ConvertKitToProduct",

        // Additional well-known Microsoft.Crm.Sdk.Messages.*Request business messages (still non-exhaustive).
        "Assign", "SetState", "Book", "Reschedule",
        "PublishXml", "PublishAllXml", "PublishDuplicateRule",
        "InstantiateTemplate", "BackgroundSendEmail", "DeliverImmediatePromoteEmail",
        "CalculatePrice", "CalculateActualValueOpportunity", "GetTrackingToken", "RetrieveExchangeRate",
        "AddPrivilegesRole", "RemovePrivilegeRole", "ReplacePrivilegesRole", "RetrieveRolePrivilegesRole",
        "AddUserToRecordTeam", "RemoveUserFromRecordTeam",
        "GrantAccessUsingConnectionRole", "ImportSolution", "ExportSolution", "AddSolutionComponent",
    };

    /// <summary>
    /// True when the Dataverse SDK already ships a request type for <paramref name="messageName"/>: see
    /// <see cref="SdkRequestName"/>.
    /// </summary>
    public static bool HasSdkRequestClass(string messageName) => SdkRequestName(messageName) != null;

    /// <summary>
    /// The name of the SDK request that runs as <paramref name="messageName"/> and ships a <c>&lt;name&gt;Request</c>
    /// type, or null when there is none. The message is an <c>sdkmessage</c> name, which is not always the request
    /// name: <c>Send</c> is run by <c>SendEmailRequest</c>, <c>Win</c> by <c>WinOpportunityRequest</c> and
    /// <c>WinQuoteRequest</c> (see <see cref="Vela.Steps.SdkMessageNames"/>). A request type is found by
    /// reflection in <c>Microsoft.Xrm.Sdk.Messages</c> (e.g. <c>"Create"</c> → <c>CreateRequest</c>) or listed in the
    /// curated <c>Microsoft.Crm.Sdk.Messages</c> supplement (e.g. <c>"QualifyLead"</c>). See the class remarks for why
    /// the supplement exists and why it is non-exhaustive.
    /// </summary>
    public static string? SdkRequestName(string messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            return null;
        }

        foreach (string requestName in new[] { messageName }.Concat(Vela.Steps.SdkMessageNames.RequestNamesFor(messageName)))
        {
            if (SdkAssembly.GetType($"Microsoft.Xrm.Sdk.Messages.{requestName}Request") != null
                || CrmSdkRequestMessages.Contains(requestName))
            {
                return requestName;
            }
        }

        return null;
    }
}
