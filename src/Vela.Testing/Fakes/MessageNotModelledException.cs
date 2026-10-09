using System;

namespace Vela.Testing
{
    /// <summary>
    /// Thrown by <see cref="InMemoryOrganizationService.Execute"/> for a request it does not simulate. It derives from
    /// <see cref="NotSupportedException"/>, so code catching that keeps working, but it is a distinct signal: the
    /// pipeline treats only this one as "no data effect to simulate" and lets every other failure propagate.
    /// </summary>
    public sealed class MessageNotModelledException : NotSupportedException
    {
        /// <summary>Creates the exception with the message naming the unsupported request.</summary>
        public MessageNotModelledException(string message)
            : base(message)
        {
        }
    }
}
