namespace Vela.Constants
{
    /// <summary>
    /// SDK message name constants.
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
        /// SetState message. Deprecated by the platform, and a step registered on it never fires: Dataverse runs a
        /// <c>SetStateRequest</c> and an Update of <c>statecode</c> as <see cref="SetStateDynamicEntity"/> (verified
        /// live). Register on <c>OnUpdate().FilterOn(x =&gt; x.StateCode)</c> or on
        /// <c>OnMessage(PluginMessages.SetStateDynamicEntity)</c> instead (analyzer VELA022).
        /// </summary>
        public const string SetState = "SetState";

        /// <summary>
        /// SetStateDynamicEntity message: the one a state change fires (a <c>SetStateRequest</c>, or an Update of
        /// <c>statecode</c>, where it runs nested at depth + 1). Its inputs are <c>EntityMoniker</c>, <c>State</c> and
        /// <c>Status</c> (-1 for the state's default). No typed method - register via
        /// <c>OnMessage(PluginMessages.SetStateDynamicEntity)</c>.
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

        /// <summary>WinOpportunity message.</summary>
        public const string WinOpportunity = "WinOpportunity";

        /// <summary>LoseOpportunity message.</summary>
        public const string LoseOpportunity = "LoseOpportunity";

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

        /// <summary>SendEmail message.</summary>
        public const string SendEmail = "SendEmail";

        /// <summary>SendEmailFromTemplate message.</summary>
        public const string SendEmailFromTemplate = "SendEmailFromTemplate";

        /// <summary>DeliverIncomingEmail message.</summary>
        public const string DeliverIncomingEmail = "DeliverIncomingEmail";

        /// <summary>DeliverPromoteEmail message.</summary>
        public const string DeliverPromoteEmail = "DeliverPromoteEmail";

        /// <summary>AddMembersTeam message.</summary>
        public const string AddMembersTeam = "AddMembersTeam";

        /// <summary>RemoveMembersTeam message.</summary>
        public const string RemoveMembersTeam = "RemoveMembersTeam";

        /// <summary>AddListMembersList message.</summary>
        public const string AddListMembersList = "AddListMembersList";

        /// <summary>AddMemberList message.</summary>
        public const string AddMemberList = "AddMemberList";

        /// <summary>RemoveMemberList message.</summary>
        public const string RemoveMemberList = "RemoveMemberList";

        /// <summary>CloseIncident message.</summary>
        public const string CloseIncident = "CloseIncident";

        /// <summary>CancelContract message.</summary>
        public const string CancelContract = "CancelContract";

        /// <summary>CloneContract message.</summary>
        public const string CloneContract = "CloneContract";

        /// <summary>RetrievePrincipalAccess message.</summary>
        public const string RetrievePrincipalAccess = "RetrievePrincipalAccess";

        /// <summary>RetrieveSharedPrincipalsAndAccess message.</summary>
        public const string RetrieveSharedPrincipalsAndAccess = "RetrieveSharedPrincipalsAndAccess";

        /// <summary>WhoAmI message.</summary>
        public const string WhoAmI = "WhoAmI";

        /// <summary>WinQuote message.</summary>
        public const string WinQuote = "WinQuote";

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
