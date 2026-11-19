using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// Builds the <see cref="FaultException{OrganizationServiceFault}"/> the platform raises, with its error code, so
    /// a test or a plugin that branches on <c>ErrorCode</c> sees what it would see against Dataverse.
    /// </summary>
    internal static class PlatformFault
    {
        /// <summary>0x80040203 InvalidArgument: a required value is missing or not allowed.</summary>
        public const int InvalidArgument = unchecked((int)0x80040203);

        /// <summary>0x80040216: raised for a relationship name the metadata does not know (verified live).</summary>
        public const int UnknownRelationship = unchecked((int)0x80040216);

        /// <summary>0x80041103: the table has no column of that name (verified live for ownerid on an org-owned table).</summary>
        public const int NoSuchAttribute = unchecked((int)0x80041103);

        /// <summary>0x80040800: the message is not supported for the table (verified live for Assign on an org-owned table).</summary>
        public const int MessageNotSupportedForEntity = unchecked((int)0x80040800);

        /// <summary>0x80048342: AddUserToRecordTeam for a user without the template's privileges (verified live).</summary>
        public const int AccessTeamMemberLacksPrivilege = unchecked((int)0x80048342);

        /// <summary>0x80045301: Merge without Write, AppendTo or Share on the table (verified live).</summary>
        public const int MergePrivilegeDenied = unchecked((int)0x80045301);

        /// <summary>0x80045304: the Merge subordinate is already inactive (verified live).</summary>
        public const int MergeSubordinateInactive = unchecked((int)0x80045304);

        // Marks a fault raised by the Read check a write makes before writing (an Update of a row the caller cannot
        // read). The platform treats it like a failed read: a plugin that catches it can carry on (verified live).
        private const string ReadCheckKey = "Vela.Testing.ReadCheck";

        public static T AsReadCheck<T>(T fault)
            where T : Exception
        {
            fault.Data[ReadCheckKey] = true;
            return fault;
        }

        public static bool IsReadCheck(Exception exception) => exception.Data.Contains(ReadCheckKey);

        public static FaultException<OrganizationServiceFault> Create(int errorCode, string message)
            => new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = message },
                new FaultReason(message));

        // The fault an ExecuteTransaction raises when one of its children fails (verified live): the detail is an
        // ExecuteTransactionFault carrying the child's ErrorCode and message and the zero-based index of the child.
        // Only a platform fault is wrapped; anything else (a plugin's InvalidPluginExecutionException, a harness
        // configuration error) returns null so it surfaces as itself, as it does for a single request.
        public static FaultException<OrganizationServiceFault>? ExecuteTransactionFailed(Exception child, int index)
        {
            if (child is not FaultException<OrganizationServiceFault> fault || fault.Detail == null)
            {
                return null;
            }

            ExecuteTransactionFault detail = new ExecuteTransactionFault
            {
                ErrorCode = fault.Detail.ErrorCode,
                Message = fault.Detail.Message,
                FaultedRequestIndex = index,
                InnerFault = fault.Detail.InnerFault,
            };
            return new FaultException<OrganizationServiceFault>(detail, new FaultReason(fault.Detail.Message ?? fault.Message));
        }
    }
}
