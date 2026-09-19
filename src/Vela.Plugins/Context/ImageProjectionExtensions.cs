using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// Reads a step's pre/post image as a generated projection type that exposes only the columns the step
    /// captured in <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>. Reading a column the step did not capture
    /// is a compile error on the projection (instead of a silent <c>null</c> off the full early-bound image), and
    /// completion is filtered to what was captured. Mirrors the existing early-bound
    /// <see cref="PluginContextExtensions.GetPreImage{T}"/> accessors, so the step declaration keeps the usual
    /// shape: <c>...WithPostImage(a =&gt; a.Name).Handle(OnUpdated)</c>, and the handler reads the projection here.
    /// </summary>
    public static class ImageProjectionExtensions
    {
        /// <summary>Projects the pre-image onto <typeparamref name="TImage"/> (the generated projection type).</summary>
        public static TImage? PreImageAs<TImage>(this ILocalPluginContext context, string alias = PluginImageAliases.PreImage)
            where TImage : class, new()
            => ImageProjector.Project<TImage>(context.GetPreImage(alias));

        /// <summary>Projects the post-image onto <typeparamref name="TImage"/> (the generated projection type).</summary>
        public static TImage? PostImageAs<TImage>(this ILocalPluginContext context, string alias = PluginImageAliases.PostImage)
            where TImage : class, new()
            => ImageProjector.Project<TImage>(context.GetPostImage(alias));
    }
}
