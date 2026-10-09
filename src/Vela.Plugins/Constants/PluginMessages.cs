namespace Vela.Constants
{
    /// <summary>
    /// The <c>sdkmessage</c> names a plugin step registers on, which is also the <c>MessageName</c> the plugin sees.
    /// Each constant is named after the SDK request (<c>SendEmail</c>), and its value is the sdkmessage, which is
    /// shorter for some requests (<c>Send</c>).
    /// </summary>
    public static class PluginMessages
    {
        /// <summary>
        /// Create message.
        /// </summary>
        public const string Create = "Create";

        /// <summary>
        /// Update message.
        /// </summary>
        public const string Update = "Update";

        /// <summary>
        /// CreateMultiple message (bulk create). A step registered on this message fires once per request
        /// with the whole <c>Targets</c> collection; Dataverse also runs single <c>Create</c> steps per row.
        /// </summary>
        public const string CreateMultiple = "CreateMultiple";

        /// <summary>
        /// UpdateMultiple message (bulk update). A step registered on this message fires once per request
        /// with the whole <c>Targets</c> collection; Dataverse also runs single <c>Update</c> steps per row.
        /// </summary>
        public const string UpdateMultiple = "UpdateMultiple";

        /// <summary>
        /// Delete message.
        /// </summary>
        public const string Delete = "Delete";

        /// <summary>
        /// Retrieve message.
        /// </summary>
        public const string Retrieve = "Retrieve";

        /// <summary>
        /// RetrieveMultiple message.
        /// </summary>
        public const string RetrieveMultiple = "RetrieveMultiple";

        /// <summary>
        /// Associate message.
        /// </summary>
        public const string Associate = "Associate";

        /// <summary>
        /// Disassociate message.
        /// </summary>
        public const string Disassociate = "Disassociate";

        /// <summary>
        /// SetState message. Deprecated by the platform - update <c>statecode</c>/<c>statuscode</c> with an
        /// Update instead. Vela does not expose a typed <c>OnSetState()</c> method; if you must target the
        /// legacy message, register it via <c>OnMessage(PluginMessages.SetState)</c>.
        /// </summary>
        public const string SetState = "SetState";

        /// <summary>
        /// SetStateDynamicEntity message. Deprecated by the platform (see <see cref="SetState"/>); no typed
        /// method - register via <c>OnMessage(PluginMessages.SetStateDynamicEntity)</c> if required.
        /// </summary>
        public const string SetStateDynamicEntity = "SetStateDynamicEntity";

        /// <summary>
        /// Assign message.
        /// </summary>
        public const string Assign = "Assign";

        /// <summary>
        /// GrantAccess message.
        /// </summary>
        public const string GrantAccess = "GrantAccess";

        /// <summary>
        /// RevokeAccess message.
        /// </summary>
        public const string RevokeAccess = "RevokeAccess";

        /// <summary>QualifyLead message.</summary>
        public const string QualifyLead = "QualifyLead";

        /// <summary><c>Win</c>: the sdkmessage of a <c>WinOpportunityRequest</c> (on opportunity). The SDK request name is <c>WinOpportunity</c>, but steps register on <c>Win</c>.</summary>
        public const string WinOpportunity = "Win";

        /// <summary><c>Lose</c>: the sdkmessage of a <c>LoseOpportunityRequest</c>. The SDK request name is <c>LoseOpportunity</c>, but steps register on <c>Lose</c>.</summary>
        public const string LoseOpportunity = "Lose";

        /// <summary>ConvertSalesOrderToInvoice message.</summary>
        public const string ConvertSalesOrderToInvoice = "ConvertSalesOrderToInvoice";

        /// <summary>GenerateQuoteFromOpportunity message.</summary>
        public const string GenerateQuoteFromOpportunity = "GenerateQuoteFromOpportunity";

        /// <summary>AddToQueue message.</summary>
        public const string AddToQueue = "AddToQueue";

        /// <summary>RemoveFromQueue message.</summary>
        public const string RemoveFromQueue = "RemoveFromQueue";

        /// <summary>PickFromQueue message.</summary>
        public const string PickFromQueue = "PickFromQueue";

        /// <summary>RouteTo message.</summary>
        public const string RouteTo = "RouteTo";

        /// <summary>ReleaseToQueue message.</summary>
        public const string ReleaseToQueue = "ReleaseToQueue";

        /// <summary><c>Send</c>: the sdkmessage of a <c>SendEmailRequest</c> (also of SendFax and SendTemplate). The SDK request name is <c>SendEmail</c>, but steps register on <c>Send</c>.</summary>
        public const string SendEmail = "Send";

        /// <summary><c>SendFromTemplate</c>: the sdkmessage of a <c>SendEmailFromTemplateRequest</c>. The SDK request name is <c>SendEmailFromTemplate</c>, but steps register on <c>SendFromTemplate</c>.</summary>
        public const string SendEmailFromTemplate = "SendFromTemplate";

        /// <summary><c>DeliverIncoming</c>: the sdkmessage of a <c>DeliverIncomingEmailRequest</c>. The SDK request name is <c>DeliverIncomingEmail</c>, but steps register on <c>DeliverIncoming</c>.</summary>
        public const string DeliverIncomingEmail = "DeliverIncoming";

        /// <summary><c>DeliverPromote</c>: the sdkmessage of a <c>DeliverPromoteEmailRequest</c>. The SDK request name is <c>DeliverPromoteEmail</c>, but steps register on <c>DeliverPromote</c>.</summary>
        public const string DeliverPromoteEmail = "DeliverPromote";

        /// <summary><c>AddMembers</c>: the sdkmessage of an <c>AddMembersTeamRequest</c>. The SDK request name is <c>AddMembersTeam</c>, but steps register on <c>AddMembers</c>.</summary>
        public const string AddMembersTeam = "AddMembers";

        /// <summary><c>RemoveMembers</c>: the sdkmessage of a <c>RemoveMembersTeamRequest</c>. The SDK request name is <c>RemoveMembersTeam</c>, but steps register on <c>RemoveMembers</c>.</summary>
        public const string RemoveMembersTeam = "RemoveMembers";

        /// <summary><c>AddListMembers</c>: the sdkmessage of an <c>AddListMembersListRequest</c>. The SDK request name is <c>AddListMembersList</c>, but steps register on <c>AddListMembers</c>.</summary>
        public const string AddListMembersList = "AddListMembers";

        /// <summary><c>AddMember</c>: the sdkmessage of an <c>AddMemberListRequest</c>. The SDK request name is <c>AddMemberList</c>, but steps register on <c>AddMember</c>.</summary>
        public const string AddMemberList = "AddMember";

        /// <summary><c>RemoveMember</c>: the sdkmessage of a <c>RemoveMemberListRequest</c>. The SDK request name is <c>RemoveMemberList</c>, but steps register on <c>RemoveMember</c>.</summary>
        public const string RemoveMemberList = "RemoveMember";

        /// <summary><c>Close</c>: the sdkmessage of a <c>CloseIncidentRequest</c> (on incident; CloseQuote shares it). The SDK request name is <c>CloseIncident</c>, but steps register on <c>Close</c>.</summary>
        public const string CloseIncident = "Close";

        /// <summary><c>Cancel</c>: the sdkmessage of a <c>CancelContractRequest</c> (on contract; CancelSalesOrder shares it). The SDK request name is <c>CancelContract</c>, but steps register on <c>Cancel</c>.</summary>
        public const string CancelContract = "Cancel";

        /// <summary><c>Clone</c>: the sdkmessage of a <c>CloneContractRequest</c>. The SDK request name is <c>CloneContract</c>, but steps register on <c>Clone</c>.</summary>
        public const string CloneContract = "Clone";

        /// <summary>RetrievePrincipalAccess message.</summary>
        public const string RetrievePrincipalAccess = "RetrievePrincipalAccess";

        /// <summary>RetrieveSharedPrincipalsAndAccess message.</summary>
        public const string RetrieveSharedPrincipalsAndAccess = "RetrieveSharedPrincipalsAndAccess";

        /// <summary>WhoAmI message.</summary>
        public const string WhoAmI = "WhoAmI";

        /// <summary><c>Win</c>: the sdkmessage of a <c>WinQuoteRequest</c> (on quote). The SDK request name is <c>WinQuote</c>, but steps register on <c>Win</c>.</summary>
        public const string WinQuote = "Win";

        /// <summary>
        /// ModifyAccess message.
        /// </summary>
        public const string ModifyAccess = "ModifyAccess";

        /// <summary>
        /// Merge message.
        /// </summary>
        public const string Merge = "Merge";

        /// <summary>
        /// The messages that are not bound to a primary entity: their sdkmessagefilter has primaryobjecttypecode
        /// "none", so a step registered on them fires for every relationship regardless of the entity it was declared
        /// on (Associate/Disassociate). The single source of truth shared by the runtime step resolution, the
        /// in-memory test pipeline and the CLI (which links this file).
        /// </summary>
        public static readonly string[] GlobalMessages = { Associate, Disassociate };

        /// <summary>
        /// Whether <paramref name="message"/> is one of the <see cref="GlobalMessages"/> (compared case-insensitively),
        /// i.e. a step registered on it is matched regardless of the primary entity.
        /// </summary>
        public static bool IsGlobalMessage(string? message)
        {
            foreach (string global in GlobalMessages)
            {
                if (string.Equals(global, message, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
