using System;
using System.Collections.Generic;

namespace Vela.Steps
{
    /// <summary>
    /// Maps an SDK request name (<c>OrganizationRequest.RequestName</c>, e.g. <c>SendEmail</c>) to the sdkmessage a
    /// plugin step registers on and sees as <c>MessageName</c> at runtime (e.g. <c>Send</c>). The two differ for a
    /// set of business messages; registering a step under the request name fails because no such sdkmessage exists.
    /// The builder applies it to every trigger, so <c>StepDefinition.Message</c> (what the CLI deploys) is
    /// already the sdkmessage; the test harness uses it to run a request's steps under the platform's name.
    /// </summary>
    /// <remarks>
    /// Read live from a Dataverse dev environment's metadata (2026-10-09): every <c>sdkmessagerequest</c> whose name
    /// differs from its <c>sdkmessage</c>, on a non-private message with a visible filter that allows a custom step.
    /// None of the request names exists as an sdkmessage, and the comment beside each entry is the request's
    /// entity (<c>global</c> when it has none). <c>SetState</c> is the one request name that is also an sdkmessage:
    /// its request lives under <c>SetStateDynamicEntity</c>, and a step on the <c>SetState</c> sdkmessage never fires
    /// (verified live: a <c>SetStateRequest</c> fires the <c>SetStateDynamicEntity</c> steps), so it maps there too;
    /// only a type pinned with <c>[SdkMessage("SetState")]</c> still lands on it (analyzer VELA022). Several request names
    /// share one sdkmessage (<c>Close</c> serves both <c>CloseIncident</c> and <c>CloseQuote</c>); the step's
    /// entity tells them apart, so the runtime keeps the request name to build the right typed context. A request
    /// type outside this table states its sdkmessage with <c>[SdkMessage("...")]</c> (<c>Vela.Messages</c>). This file
    /// is linked into the CLI so <c>vela models --messages</c> emits that attribute only for a request the table does
    /// not know.
    /// </remarks>
    internal static partial class SdkMessageNames
    {
        private static readonly Dictionary<string, string> RequestToMessage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AddItemCampaign"] = "AddItem",                              // campaign
            ["AddItemCampaignActivity"] = "AddItem",                      // campaignactivity
            ["AddListMembersList"] = "AddListMembers",                    // list
            ["AddMemberList"] = "AddMember",                              // list
            ["AddMembersBatchList"] = "AddMembersBatch",                  // list
            ["AddMembersTeam"] = "AddMembers",                            // team
            ["AddPrivilegesRole"] = "AddPrivileges",                      // role
            ["BackgroundSendEmail"] = "BackgroundSend",                   // global
            ["CancelContract"] = "Cancel",                                // contract
            ["CancelSalesOrder"] = "Cancel",                              // salesorder
            ["CheckIncomingEmail"] = "CheckIncoming",                     // email
            ["CheckPromoteEmail"] = "CheckPromote",                       // email
            ["CloneContract"] = "Clone",                                  // contract
            ["CloseIncident"] = "Close",                                  // incident
            ["CloseQuote"] = "Close",                                     // quote
            ["DeliverImmediatePromoteEmail"] = "DeliverImmediatePromote", // email
            ["DeliverIncomingEmail"] = "DeliverIncoming",                 // email
            ["DeliverPromoteEmail"] = "DeliverPromote",                   // email
            ["ExecuteByIdSavedQuery"] = "ExecuteById",                    // savedquery
            ["ExecuteByIdUserQuery"] = "ExecuteById",                     // userquery
            ["ExecuteFetch"] = "Execute",                                 // global
            ["LoseOpportunity"] = "Lose",                                 // opportunity
            ["PublishAllXml"] = "PublishAll",                             // global
            ["PublishDuplicateRule"] = "Publish",                         // duplicaterule
            ["PublishXml"] = "Publish",                                   // global
            ["RemoveItemCampaign"] = "RemoveItem",                        // campaign
            ["RemoveItemCampaignActivity"] = "RemoveItem",                // campaignactivity
            ["RemoveListMembersList"] = "RemoveListMembers",              // list
            ["RemoveMemberList"] = "RemoveMember",                        // list
            ["RemoveMembersTeam"] = "RemoveMembers",                      // team
            ["RemovePrivilegeRole"] = "RemovePrivilege",                  // role
            ["ReplacePrivilegesRole"] = "ReplacePrivileges",              // role
            ["SendEmail"] = "Send",                                       // email
            ["SendEmailFromTemplate"] = "SendFromTemplate",               // email
            ["SendFax"] = "Send",                                         // fax
            ["SendTemplate"] = "Send",                                    // template
            ["SetState"] = "SetStateDynamicEntity",                       // global; SetState itself never fires (VELA022)
            ["ValidateSavedQuery"] = "Validate",                          // savedquery
            ["ValidateUnpublished"] = "Validate",                         // global
            ["WinOpportunity"] = "Win",                                   // opportunity
            ["WinQuote"] = "Win",                                         // quote
        };

        /// <summary>
        /// The sdkmessage a step for <paramref name="requestName"/> registers on. A name that is already an
        /// sdkmessage (Create, Win, a custom API, ...) is returned unchanged.
        /// </summary>
        internal static string ToSdkMessage(string requestName)
        {
            if (requestName == null)
            {
                return string.Empty;
            }

            string trimmed = requestName.Trim();
            return RequestToMessage.TryGetValue(trimmed, out string? message) ? message : trimmed;
        }
    }
}
