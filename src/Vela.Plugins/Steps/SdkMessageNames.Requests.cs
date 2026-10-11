using System.Reflection;
using Microsoft.Xrm.Sdk;
using Vela.Messages;

namespace Vela.Steps
{
    internal static partial class SdkMessageNames
    {
        /// <summary>
        /// The sdkmessage <paramref name="request"/> runs on: its type's <see cref="SdkMessageAttribute"/> when
        /// present, else <see cref="ToSdkMessage(string)"/> of its <c>RequestName</c>.
        /// </summary>
        internal static string ToSdkMessage(OrganizationRequest request)
        {
            SdkMessageAttribute? declared = request.GetType().GetCustomAttribute<SdkMessageAttribute>(inherit: true);
            return declared != null ? declared.MessageName : ToSdkMessage(request.RequestName);
        }
    }
}
