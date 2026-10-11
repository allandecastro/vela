using Vela.Constants;

namespace Vela.Tests.TestSupport
{
    /// <summary>Marker for a custom, non-modelled message (<c>OnMessage&lt;AcmeDoThing&gt;()</c>).</summary>
    [CustomMessage("acme_DoThing")]
    internal sealed class AcmeDoThing
    {
    }
}
