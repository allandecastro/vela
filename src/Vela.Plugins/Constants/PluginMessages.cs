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

        /// <summary>WinOpportunity message.</summary>
        public const string WinOpportunity = "WinOpportunity";

        /// <summary>LoseOpportunity message.</summary>
        public const string LoseOpportunity = "LoseOpportunity";

        /// <summary>ConvertQuoteToSalesOrder message.</summary>
        public const string ConvertQuoteToSalesOrder = "ConvertQuoteToSalesOrder";

        /// <summary>ConvertSalesOrderToInvoice message.</summary>
        public const string ConvertSalesOrderToInvoice = "ConvertSalesOrderToInvoice";

        /// <summary>GenerateQuoteFromOpportunity message.</summary>
        public const string GenerateQuoteFromOpportunity = "GenerateQuoteFromOpportunity";

        /// <summary>GenerateSalesOrderFromOpportunity message.</summary>
        public const string GenerateSalesOrderFromOpportunity = "GenerateSalesOrderFromOpportunity";

        /// <summary>GenerateInvoiceFromOpportunity message.</summary>
        public const string GenerateInvoiceFromOpportunity = "GenerateInvoiceFromOpportunity";

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

        /// <summary>ReviseQuote message.</summary>
        public const string ReviseQuote = "ReviseQuote";

        /// <summary>FulfillSalesOrder message.</summary>
        public const string FulfillSalesOrder = "FulfillSalesOrder";

        /// <summary>GetQuoteProductsFromOpportunity message.</summary>
        public const string GetQuoteProductsFromOpportunity = "GetQuoteProductsFromOpportunity";

        /// <summary>GetSalesOrderProductsFromOpportunity message.</summary>
        public const string GetSalesOrderProductsFromOpportunity = "GetSalesOrderProductsFromOpportunity";

        /// <summary>GetInvoiceProductsFromOpportunity message.</summary>
        public const string GetInvoiceProductsFromOpportunity = "GetInvoiceProductsFromOpportunity";

        /// <summary>
        /// ModifyAccess message.
        /// </summary>
        public const string ModifyAccess = "ModifyAccess";

        /// <summary>
        /// Merge message.
        /// </summary>
        public const string Merge = "Merge";
    }
}
