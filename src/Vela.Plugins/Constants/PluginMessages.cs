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
        /// SetState message.
        /// </summary>
        public const string SetState = "SetState";

        /// <summary>
        /// SetStateDynamicEntity message.
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

        /// <summary>
        /// ModifyAccess message.
        /// </summary>
        public const string ModifyAccess = "ModifyAccess";
    }
}
