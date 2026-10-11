using System;
using System.Collections.Generic;

namespace Vela.Steps
{
    /// <summary>
    /// Maps an SDK request name (<c>OrganizationRequest.RequestName</c>, e.g. <c>SendEmail</c>) to the sdkmessage a
    /// plugin step registers on and sees as <c>MessageName</c> at runtime (e.g. <c>Send</c>), and to the table the
    /// request runs on. The two names differ for many business messages; registering a step under the request name
    /// fails because no such sdkmessage exists. The builder applies it to every trigger, so
    /// <see cref="StepDefinition.Message"/> (what the CLI deploys) is already the sdkmessage; the test harness uses it
    /// to run a request's steps under the platform's name and on the platform's table. Shared as linked source with
    /// the CLI.
    /// </summary>
    /// <remarks>
    /// Read from the <c>sdkmessagerequest</c> table of a Dataverse dev environment (9.2, 2026-10-09): every public
    /// request whose name is not itself an <c>sdkmessage</c>, with the table its request is bound to (null when it is
    /// not bound to one). A request name that is also an sdkmessage name (<c>SetState</c>, <c>Create</c>, ...) is not
    /// listed, as a step on it is registered under that same name, except the modelled messages bound to a single
    /// table, listed for their table only. Several request names share one sdkmessage (<c>Win</c> serves both
    /// <c>WinOpportunity</c> and <c>WinQuote</c>); the step's table tells them apart, so the runtime keeps the request
    /// name to build the right typed context. Verified live through deployed steps on every modelled message
    /// (MessageName and PrimaryEntityName as listed here, PrimaryEntityId always empty): the queue messages run on
    /// <c>queueitem</c> whatever their Target.
    /// </remarks>
    internal static class SdkMessageNames
    {
        private static readonly Dictionary<string, Entry> ByRequestName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            // Modelled messages whose name is the sdkmessage, listed for the single table they run on.
            ["AddToQueue"] = new Entry("AddToQueue", "queueitem"),
            ["ConvertSalesOrderToInvoice"] = new Entry("ConvertSalesOrderToInvoice", "salesorder"),
            ["GenerateQuoteFromOpportunity"] = new Entry("GenerateQuoteFromOpportunity", "quote"),
            ["PickFromQueue"] = new Entry("PickFromQueue", "queueitem"),
            ["QualifyLead"] = new Entry("QualifyLead", "lead"),
            ["ReleaseToQueue"] = new Entry("ReleaseToQueue", "queueitem"),
            ["RemoveFromQueue"] = new Entry("RemoveFromQueue", "queueitem"),
            ["RouteTo"] = new Entry("RouteTo", "queueitem"),

            // Requests the platform runs under another sdkmessage name.
            ["AddItemCampaign"] = new Entry("AddItem", "campaign"),
            ["AddItemCampaignActivity"] = new Entry("AddItem", "campaignactivity"),
            ["AddListMembersList"] = new Entry("AddListMembers", "list"),
            ["AddMemberList"] = new Entry("AddMember", "list"),
            ["AddMembersBatchList"] = new Entry("AddMembersBatch", "list"),
            ["AddMembersTeam"] = new Entry("AddMembers", "team"),
            ["AddPrivilegesRole"] = new Entry("AddPrivileges", "role"),
            ["AddSubstituteProduct"] = new Entry("AddSubstitute", "product"),
            ["AssignUserRolesRole"] = new Entry("AssignUserRoles", null),
            ["BackgroundSendEmail"] = new Entry("BackgroundSend", null),
            ["CalculateActualValueOpportunity"] = new Entry("CalculateActualValue", "opportunity"),
            ["CalculateTotalTimeIncident"] = new Entry("CalculateTotalTime", "incident"),
            ["CancelContract"] = new Entry("Cancel", "contract"),
            ["CancelSalesOrder"] = new Entry("Cancel", "salesorder"),
            ["CheckIncomingEmail"] = new Entry("CheckIncoming", "email"),
            ["CheckPromoteEmail"] = new Entry("CheckPromote", "email"),
            ["CloneContract"] = new Entry("Clone", "contract"),
            ["CloseIncident"] = new Entry("Close", "incident"),
            ["CloseQuote"] = new Entry("Close", "quote"),
            ["CopyCampaign"] = new Entry("Copy", "campaign"),
            ["CopyMembersList"] = new Entry("CopyMembers", "list"),
            ["CreateActivitiesList"] = new Entry("CreateActivities", "list"),
            ["DeliverImmediatePromoteEmail"] = new Entry("DeliverImmediatePromote", "email"),
            ["DeliverIncomingEmail"] = new Entry("DeliverIncoming", "email"),
            ["DeliverPromoteEmail"] = new Entry("DeliverPromote", "email"),
            ["DetachFromQueueEmail"] = new Entry("DetachFromQueue", null),
            ["ExecuteByIdSavedQuery"] = new Entry("ExecuteById", "savedquery"),
            ["ExecuteByIdUserQuery"] = new Entry("ExecuteById", "userquery"),
            ["ExecuteCampaignActivity"] = new Entry("Execute", null),
            ["ExecuteFetch"] = new Entry("Execute", null),
            ["ExpandCalendar"] = new Entry("Expand", "calendar"),
            ["ExportAllXml"] = new Entry("ExportAll", null),
            ["ExportCompressedAllXml"] = new Entry("ExportCompressedAll", null),
            ["ExportCompressedTranslationsXml"] = new Entry("ExportCompressedTranslations", null),
            ["ExportCompressedXml"] = new Entry("ExportCompressed", null),
            ["ExportMappingsImportMap"] = new Entry("ExportMappings", "importmap"),
            ["ExportTranslationsXml"] = new Entry("ExportTranslations", null),
            ["ExportXml"] = new Entry("Export", null),
            ["FetchXmlToQueryExpression"] = new Entry("FetchXmlToEntityExpression", null),
            ["FindParentResourceGroup"] = new Entry("FindParent", "resourcegroup"),
            ["FulfillSalesOrder"] = new Entry("Fulfill", "salesorder"),
            ["GetDistinctValuesImportFile"] = new Entry("GetDistinctValues", "importfile"),
            ["GetHeaderColumnsImportFile"] = new Entry("GetHeaderColumns", "importfile"),
            ["GetTrackingTokenEmail"] = new Entry("GetTrackingToken", "email"),
            ["ImportAllXml"] = new Entry("ImportAll", null),
            ["ImportCompressedAllXml"] = new Entry("ImportCompressedAll", null),
            ["ImportCompressedTranslationsXmlWithProgress"] = new Entry("ImportCompressedTranslationsWithProgress", null),
            ["ImportCompressedXmlWithProgress"] = new Entry("ImportCompressedWithProgress", null),
            ["ImportMappingsImportMap"] = new Entry("ImportMappings", "importmap"),
            ["ImportRecordsImport"] = new Entry("ImportRecords", "import"),
            ["ImportTranslationsXmlWithProgress"] = new Entry("ImportTranslationsWithProgress", null),
            ["ImportXml"] = new Entry("Import", null),
            ["ImportXmlWithProgress"] = new Entry("ImportWithProgress", null),
            ["InstantiateHtmlContent"] = new Entry("InstantiateHtml", "template"),
            ["InstantiateTemplate"] = new Entry("Instantiate", "template"),
            ["InstantiateTemplateGeneric"] = new Entry("InstantiateGeneric", "template"),
            ["LoseOpportunity"] = new Entry("Lose", "opportunity"),
            ["MakeAvailableToOrganizationReport"] = new Entry("MakeAvailableToOrganization", "report"),
            ["MakeAvailableToOrganizationTemplate"] = new Entry("MakeAvailableToOrganization", "template"),
            ["MakeUnavailableToOrganizationReport"] = new Entry("MakeUnavailableToOrganization", "report"),
            ["MakeUnavailableToOrganizationTemplate"] = new Entry("MakeUnavailableToOrganization", "template"),
            ["ParseImport"] = new Entry("Parse", "import"),
            ["ProcessInboundEmail"] = new Entry("ProcessInbound", "email"),
            ["PublishAllXml"] = new Entry("PublishAll", null),
            ["PublishAllXmlAsync"] = new Entry("PublishAllAsync", null),
            ["PublishDuplicateRule"] = new Entry("Publish", "duplicaterule"),
            ["PublishXml"] = new Entry("Publish", null),
            ["QualifyMemberList"] = new Entry("QualifyMember", "list"),
            ["QueryExpressionToFetchXml"] = new Entry("EntityExpressionToFetchXml", null),
            ["QueryMultipleSchedules"] = new Entry("QueryMultiple", null),
            ["QuerySchedule"] = new Entry("Query", null),
            ["ReassignObjectsOwner"] = new Entry("ReassignObjectsEx", null),
            ["ReassignObjectsSystemUser"] = new Entry("ReassignObjects", "systemuser"),
            ["RemoveItemCampaign"] = new Entry("RemoveItem", "campaign"),
            ["RemoveItemCampaignActivity"] = new Entry("RemoveItem", "campaignactivity"),
            ["RemoveListMembersList"] = new Entry("RemoveListMembers", "list"),
            ["RemoveMemberList"] = new Entry("RemoveMember", "list"),
            ["RemoveMembersTeam"] = new Entry("RemoveMembers", "team"),
            ["RemovePrivilegeRole"] = new Entry("RemovePrivilege", "role"),
            ["RemoveSubstituteProduct"] = new Entry("RemoveSubstitute", "product"),
            ["RemoveUserRolesRole"] = new Entry("RemoveUserRoles", null),
            ["RenewContract"] = new Entry("Renew", "contract"),
            ["ReplacePrivilegesRole"] = new Entry("ReplacePrivileges", "role"),
            ["ResetDataFilters"] = new Entry("ResetOfflineFilters", null),
            ["RetrieveAllChildUsersSystemUser"] = new Entry("RetrieveAllChildUsers", "systemuser"),
            ["RetrieveBusinessHierarchyBusinessUnit"] = new Entry("RetrieveBusinessHierarchy", "businessunit"),
            ["RetrieveByGroupResource"] = new Entry("RetrieveByGroup", "resource"),
            ["RetrieveByResourceResourceGroup"] = new Entry("RetrieveByResource", "resourcegroup"),
            ["RetrieveByResourcesService"] = new Entry("RetrieveByResources", "service"),
            ["RetrieveByTopIncidentProductKbArticle"] = new Entry("RetrieveByTopIncidentProduct", "kbarticle"),
            ["RetrieveByTopIncidentSubjectKbArticle"] = new Entry("RetrieveByTopIncidentSubject", "kbarticle"),
            ["RetrieveMembersTeam"] = new Entry("RetrieveMembers", "team"),
            ["RetrieveParentGroupsResourceGroup"] = new Entry("RetrieveParentGroups", "resourcegroup"),
            ["RetrieveParsedDataImportFile"] = new Entry("RetrieveParsedData", "importfile"),
            ["RetrieveRolePrivilegesRole"] = new Entry("RetrieveRolePrivileges", "role"),
            ["RetrieveSubGroupsResourceGroup"] = new Entry("RetrieveSubGroups", "resourcegroup"),
            ["RetrieveSubsidiaryTeamsBusinessUnit"] = new Entry("RetrieveSubsidiaryTeams", "businessunit"),
            ["RetrieveSubsidiaryUsersBusinessUnit"] = new Entry("RetrieveSubsidiaryUsers", "businessunit"),
            ["RetrieveTeamsSystemUser"] = new Entry("RetrieveTeams", "systemuser"),
            ["RetrieveUserSettingsSystemUser"] = new Entry("RetrieveUserSettings", "systemuser"),
            ["ReviseQuote"] = new Entry("Revise", "quote"),
            ["SendBulkMail"] = new Entry("BulkMail", null),
            ["SendEmail"] = new Entry("Send", "email"),
            ["SendEmailFromTemplate"] = new Entry("SendFromTemplate", "email"),
            ["SendFax"] = new Entry("Send", "fax"),
            ["SendTemplate"] = new Entry("Send", "template"),
            ["SetBusinessEquipment"] = new Entry("SetBusiness", "equipment"),
            ["SetBusinessSystemUser"] = new Entry("SetBusiness", "systemuser"),
            ["SetParentBusinessUnit"] = new Entry("SetParent", "businessunit"),
            ["SetParentSystemUser"] = new Entry("SetParent", "systemuser"),
            ["SetParentTeam"] = new Entry("SetParent", "team"),
            ["TransformImport"] = new Entry("Transform", "import"),
            ["UnpublishDuplicateRule"] = new Entry("Unpublish", "duplicaterule"),
            ["UpdateUserSettingsSystemUser"] = new Entry("UpdateUserSettings", "systemuser"),
            ["ValidateSavedQuery"] = new Entry("Validate", "savedquery"),
            ["ValidateUnpublished"] = new Entry("Validate", null),
            ["WinOpportunity"] = new Entry("Win", "opportunity"),
            ["WinQuote"] = new Entry("Win", "quote"),
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
            return ByRequestName.TryGetValue(trimmed, out Entry? entry) ? entry.Message : trimmed;
        }

        /// <summary>
        /// The table the request runs on (for example <c>email</c> for <c>SendEmail</c>, <c>quote</c> for
        /// <c>WinQuote</c>, <c>queueitem</c> for <c>AddToQueue</c>), or null when the request is not mapped or not
        /// bound to one table.
        /// </summary>
        internal static string? PrimaryEntityOf(string requestName)
            => requestName != null && ByRequestName.TryGetValue(requestName.Trim(), out Entry? entry) ? entry.PrimaryEntity : null;

        /// <summary>
        /// The request names the platform runs as <paramref name="sdkMessage"/> under a different name (for example
        /// <c>WinOpportunity</c> and <c>WinQuote</c> for <c>Win</c>). Empty when there is none.
        /// </summary>
        internal static IEnumerable<string> RequestNamesFor(string sdkMessage)
        {
            foreach (KeyValuePair<string, Entry> pair in ByRequestName)
            {
                if (string.Equals(pair.Value.Message, sdkMessage, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, sdkMessage, StringComparison.OrdinalIgnoreCase))
                {
                    yield return pair.Key;
                }
            }
        }

        private sealed class Entry
        {
            public Entry(string message, string? primaryEntity)
            {
                Message = message;
                PrimaryEntity = primaryEntity;
            }

            public string Message { get; }

            public string? PrimaryEntity { get; }
        }
    }
}
