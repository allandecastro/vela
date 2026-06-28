using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    internal sealed class NullTracingService : ITracingService
    {
        public void Trace(string format, params object[] args)
        {
        }
    }
}
