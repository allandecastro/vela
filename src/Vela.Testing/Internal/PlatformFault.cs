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

        public static FaultException<OrganizationServiceFault> Create(int errorCode, string message)
            => new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = message },
                new FaultReason(message));
    }
}
