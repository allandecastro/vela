using Vela.DI;

namespace Vela.Context
{
    /// <summary>
    /// A context that exposes the per-execution dependency-injection resolver. Implemented by the runtime
    /// <see cref="LocalPluginContext"/> and forwarded by the typed <c>StepContext</c> wrappers, so that
    /// <c>ctx.GetService&lt;T&gt;()</c> / <c>ctx.GetRequiredService&lt;T&gt;()</c> work from any handler - including
    /// a message-typed <c>I{Message}Context</c> handler, not only the plain <see cref="LocalPluginContext"/>.
    /// </summary>
    public interface IServiceResolverContext
    {
        /// <summary>The per-execution service resolver.</summary>
        IPluginServiceResolver Resolver { get; }
    }
}
