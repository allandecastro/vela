using System;
using System.Collections.Generic;

namespace Vela.Steps
{
    /// <summary>
    /// Maps an SDK request name (<c>OrganizationRequest.RequestName</c>, e.g. <c>SendEmail</c>) to the sdkmessage a
    /// plugin step registers on and sees as <c>MessageName</c> at runtime (e.g. <c>Send</c>). The two differ for a
    /// set of business messages; registering a step under the request name fails because no such sdkmessage exists.
    /// The builder applies it to every trigger, so <see cref="StepDefinition.Message"/> (what the CLI deploys) is
    /// already the sdkmessage; the test harness uses it to run a request's steps under the platform's name.
    /// </summary>
    /// <remarks>
    /// Verified live on a Dataverse dev environment (2026-10-09): none of the request names below exists as an
    /// sdkmessage, and each sdkmessage on the right has a filter for the entity in the comment beside it. Several request names
    /// share one sdkmessage (<c>Win</c> serves both <c>WinOpportunity</c> and <c>WinQuote</c>); the step's entity tells
    /// them apart, so the runtime keeps the request name to build the right typed context.
    /// </remarks>
    internal static class SdkMessageNames
    {
        private static readonly Dictionary<string, string> RequestToMessage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SendEmail"] = "Send",                         // email
            ["SendEmailFromTemplate"] = "SendFromTemplate", // email
            ["DeliverIncomingEmail"] = "DeliverIncoming",   // email
            ["DeliverPromoteEmail"] = "DeliverPromote",     // email
            ["WinOpportunity"] = "Win",                     // opportunity
            ["WinQuote"] = "Win",                           // quote
            ["LoseOpportunity"] = "Lose",                   // opportunity
            ["CloseIncident"] = "Close",                    // incident
            ["CancelContract"] = "Cancel",                  // contract
            ["CloneContract"] = "Clone",                    // contract
            ["AddMembersTeam"] = "AddMembers",              // team
            ["RemoveMembersTeam"] = "RemoveMembers",        // team
            ["AddListMembersList"] = "AddListMembers",      // list
            ["AddMemberList"] = "AddMember",                // list
            ["RemoveMemberList"] = "RemoveMember",          // list
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
