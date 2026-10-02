using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Implemented by every generated image projection. Carries the source <see cref="Entity"/> the projection was
    /// hydrated from, so the generated explicit <c>(TEntity)projection</c> cast returns the full early-bound image
    /// losslessly (the <see cref="Entity.Id"/>, logical name and every attribute the image carried), rather than a
    /// reconstruction limited to the mapped columns. Set by <see cref="Vela.Steps.ImageProjector"/> at projection
    /// time and read through the generated cast operator.
    /// </summary>
    /// <remarks>
    /// The projection implements this <b>explicitly</b>, so <see cref="Source"/> is not part of the projection's
    /// public column surface: a handler still sees only the captured columns, and the escape to the full entity is
    /// the deliberate, visible <c>(TEntity)</c> cast - never an implicit widening that would reopen the silent-null
    /// footgun the typed projection exists to close.
    /// </remarks>
    public interface IImageProjection
    {
        /// <summary>The Dataverse image this projection was hydrated from, or <c>null</c> when built by hand.</summary>
        Entity? Source { get; set; }
    }
}
