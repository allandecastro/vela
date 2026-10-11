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
        /// live). <c>On&lt;SetStateRequest&gt;()</c> therefore registers on SetStateDynamicEntity; register on
        /// <c>OnUpdate().FilterOn(x =&gt; x.StateCode)</c> to also catch an Update of the state (analyzer VELA022).
        /// </summary>
        public const string SetState = "SetState";

        /// <summary>
        /// SetStateDynamicEntity message: the one a state change fires (a <c>SetStateRequest</c>, or an Update of
        /// <c>statecode</c>, where it runs nested at depth + 1). Its inputs are <c>EntityMoniker</c>, <c>State</c> and
        /// <c>Status</c> (-1 for the state's default). No curated method - register via
        /// <c>On&lt;SetStateRequest&gt;()</c>, which lands here.
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

        /// <summary>AddItemCampaign message (the <c>AddItem</c> sdkmessage on campaign).</summary>
        public const string AddItemCampaign = "AddItemCampaign";

        /// <summary>RemoveItemCampaign message (the <c>RemoveItem</c> sdkmessage on campaign).</summary>
        public const string RemoveItemCampaign = "RemoveItemCampaign";

        /// <summary>Recalculate message.</summary>
        public const string Recalculate = "Recalculate";

        /// <summary>LockSalesOrderPricing message.</summary>
        public const string LockSalesOrderPricing = "LockSalesOrderPricing";

        /// <summary>LockInvoicePricing message.</summary>
        public const string LockInvoicePricing = "LockInvoicePricing";

        /// <summary>Reschedule message.</summary>
        public const string Reschedule = "Reschedule";

        /// <summary>ImmediateBook message.</summary>
        public const string ImmediateBook = "ImmediateBook";

        /// <summary>SendTemplate message (the <c>Send</c> sdkmessage on template).</summary>
        public const string SendTemplate = "SendTemplate";

        /// <summary>SendFax message (the <c>Send</c> sdkmessage on fax).</summary>
        public const string SendFax = "SendFax";

        /// <summary>AddProductToKit message.</summary>
        public const string AddProductToKit = "AddProductToKit";

        /// <summary>UnlockSalesOrderPricing message.</summary>
        public const string UnlockSalesOrderPricing = "UnlockSalesOrderPricing";

        /// <summary>UnlockInvoicePricing message.</summary>
        public const string UnlockInvoicePricing = "UnlockInvoicePricing";

        /// <summary>RemoveProductFromKit message.</summary>
        public const string RemoveProductFromKit = "RemoveProductFromKit";

        /// <summary>
        /// The messages that are not bound to a primary entity: their only sdkmessagefilter has primaryobjecttypecode
        /// "none", so a step registered on them fires regardless of the entity it was declared on (Associate and
        /// Disassociate for every relationship; UnlockSalesOrderPricing, UnlockInvoicePricing and RemoveProductFromKit).
        /// The single source of truth shared by the step builder, the runtime step resolution, the in-memory test
        /// pipeline and the CLI (which links this file).
        /// </summary>
        public static readonly string[] GlobalMessages = { Associate, Disassociate, UnlockSalesOrderPricing, UnlockInvoicePricing, RemoveProductFromKit };

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
