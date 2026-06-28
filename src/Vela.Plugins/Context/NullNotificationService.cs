using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    internal sealed class NullNotificationService : IServiceEndpointNotificationService
    {
        public string Execute(EntityReference serviceEndpoint, IExecutionContext context)
        {
            return string.Empty;
        }
    }
}
