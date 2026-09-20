using System.Collections.Generic;
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

        // ---- Bulk (CreateMultiple / UpdateMultiple): per-record projections -------------------------
        // The *Multiple contexts expose images only as raw per-index collections; these project each record's
        // image onto the generated type, keeping the list index aligned with ctx.GetTargets().

        /// <summary>Projects the per-record pre-image of a bulk step onto <typeparamref name="TImage"/> for a single target index.</summary>
        public static TImage? PreImageAs<TImage>(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PreImage)
            where TImage : class, new()
            => ImageProjector.Project<TImage>(context.GetPreImage(targetIndex, alias));

        /// <summary>Projects the per-record post-image of a bulk step onto <typeparamref name="TImage"/> for a single target index.</summary>
        public static TImage? PostImageAs<TImage>(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PostImage)
            where TImage : class, new()
            => ImageProjector.Project<TImage>(context.GetPostImage(targetIndex, alias));

        /// <summary>
        /// Projects every record's pre-image of a bulk step onto <typeparamref name="TImage"/>. The list index is
        /// aligned with <c>ctx.GetTargets()</c>; an entry is <c>null</c> when that record has no such image.
        /// </summary>
        public static IReadOnlyList<TImage?> PreImagesAs<TImage>(this ILocalPluginContext context, string alias = PluginImageAliases.PreImage)
            where TImage : class, new()
            => ProjectBulk<TImage>(context, alias, isPre: true);

        /// <summary>
        /// Projects every record's post-image of a bulk step onto <typeparamref name="TImage"/>. The list index is
        /// aligned with <c>ctx.GetTargets()</c>; an entry is <c>null</c> when that record has no such image.
        /// </summary>
        public static IReadOnlyList<TImage?> PostImagesAs<TImage>(this ILocalPluginContext context, string alias = PluginImageAliases.PostImage)
            where TImage : class, new()
            => ProjectBulk<TImage>(context, alias, isPre: false);

        private static IReadOnlyList<TImage?> ProjectBulk<TImage>(ILocalPluginContext context, string alias, bool isPre)
            where TImage : class, new()
        {
            int count = context.BulkTargets?.Entities.Count ?? 0;
            List<TImage?> projections = new List<TImage?>(count);
            for (int i = 0; i < count; i++)
            {
                Entity? image = isPre ? context.GetPreImage(i, alias) : context.GetPostImage(i, alias);
                projections.Add(ImageProjector.Project<TImage>(image));
            }

            return projections;
        }
    }
}
