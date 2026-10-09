namespace Vela.Steps
{
    /// <summary>
    /// Single source of truth for which step images Dataverse supports, per message and stage, and which request
    /// property (<c>messagepropertyname</c>) each image is registered against. Shared as linked source by the
    /// runtime builder (<c>StepBuilder</c>, which the test harness consumes), the Roslyn analyzer
    /// (VELA007) and the CLI (scanner and image sync), so none of them can accept what another rejects.
    /// Self-contained on purpose (string message names, int stages) so it compiles in net462, netstandard2.0 and
    /// net10.0 without dragging in any other Vela type.
    /// </summary>
    /// <remarks>
    /// Verified live on a Dataverse dev environment (2026-10-07 and 2026-10-09):
    /// <list type="bullet">
    /// <item>Create: <c>Id</c>, post-image only (Learn says <c>Target</c>, which the platform rejects).</item>
    /// <item>CreateMultiple: <c>Ids</c>, post-image only. UpdateMultiple: <c>Targets</c>, pre- and post-image
    /// (<c>Target</c> fails with 0x8004416B).</item>
    /// <item>Send / DeliverIncoming / DeliverPromote: <c>EmailId</c>. Merge: <c>Target</c> (or
    /// <c>SubordinateId</c>; Vela always uses <c>Target</c>, see #462). SetState / SetStateDynamicEntity:
    /// <c>EntityMoniker</c>, pre- and post-image (an SDK <c>SetStateRequest</c> on account fires only the
    /// SetStateDynamicEntity step). RouteTo: no image at all (0x80044189).</item>
    /// <item>Assign: <c>Target</c>, pre- and post-image (the pre-image holds the old owner, the post-image the new).</item>
    /// <item>A post-image before PostOperation fails with 0x8004416E; a pre-image is fine at PreValidation.</item>
    /// </list>
    /// </remarks>
    internal static class StepImageRules
    {
        internal const int PreValidation = 10;
        internal const int PreOperation = 20;
        internal const int MainOperation = 30;
        internal const int PostOperation = 40;

        /// <summary>
        /// The <c>messagepropertyname</c> an image on <paramref name="message"/> is registered against, or null when
        /// the message supports no image at all. Messages Vela does not model (custom messages, custom APIs) fall
        /// back to <c>Target</c>.
        /// </summary>
        internal static string? GetMessagePropertyName(string? message)
        {
            switch ((message ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "create":
                    return "Id";
                case "createmultiple":
                    return "Ids";
                case "updatemultiple":
                    return "Targets";
                case "setstate":
                case "setstatedynamicentity":
                    return "EntityMoniker";
                case "send":
                case "deliverincoming":
                case "deliverpromote":
                    return "EmailId";
                case "routeto":
                    return null;
                default:
                    return "Target";
            }
        }

        /// <summary>
        /// Why the message can never provide this kind of image, whatever the stage, or null when it can. These are
        /// the rules a static analysis can check without knowing the stage.
        /// </summary>
        internal static string? GetMessageRejectionReason(string? message, bool isPreImage)
        {
            string normalized = (message ?? string.Empty).Trim().ToLowerInvariant();
            if (GetMessagePropertyName(normalized) is null)
            {
                return "Dataverse supports no image on this message";
            }

            if (isPreImage && (normalized == "create" || normalized == "createmultiple"))
            {
                return "there is no prior row state before a create";
            }

            if (!isPreImage && normalized == "delete")
            {
                return "the row no longer exists after a delete";
            }

            return null;
        }

        /// <summary>
        /// Why Dataverse rejects this image on a step for <paramref name="message"/> at <paramref name="stage"/>, or
        /// null when it accepts it. Stage rules apply to every message: no image exists at MainOperation, and a
        /// post-image never exists before PostOperation (0x8004416E). A pre-image is allowed at PreValidation.
        /// </summary>
        internal static string? GetRejectionReason(string? message, int stage, bool isPreImage)
        {
            if (stage == MainOperation)
            {
                return "no image exists at MainOperation";
            }

            if (!isPreImage && stage != PostOperation)
            {
                return "a post-image only exists at PostOperation, after the write";
            }

            return GetMessageRejectionReason(message, isPreImage);
        }

        /// <summary>True when Dataverse accepts this image on a step for the message at the stage.</summary>
        internal static bool IsImageAllowed(string? message, int stage, bool isPreImage)
            => GetRejectionReason(message, stage, isPreImage) is null;
    }
}
