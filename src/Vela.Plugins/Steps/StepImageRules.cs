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
    /// <item>Send / DeliverIncoming / DeliverPromote: <c>EmailId</c>. Merge: <c>Target</c> for the master record, and
    /// <c>SubordinateId</c> for the record merged away; one step can carry both, at every stage (the
    /// <c>SubordinateId</c> post-image is the deactivated subordinate). <c>SubordinateId</c> on any other message fails
    /// with 0x8004416B. SetState / SetStateDynamicEntity:
    /// <c>EntityMoniker</c>, pre- and post-image (an SDK <c>SetStateRequest</c> on account fires only the
    /// SetStateDynamicEntity step). RouteTo: no image at all (0x80044189). SendFromTemplate: no image at all
    /// ("does not support image registration").</item>
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

        /// <summary>The image property of the subordinate record of a Merge (the record merged away).</summary>
        internal const string SubordinateId = "SubordinateId";

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
                case "sendfromtemplate":
                    return null;
                default:
                    return "Target";
            }
        }

        /// <summary>
        /// The <c>messagepropertyname</c> an image is registered against: <paramref name="explicitProperty"/> when the
        /// step chose one (only <c>SubordinateId</c> on Merge today), otherwise the message's own
        /// (<see cref="GetMessagePropertyName"/>). Null when the message supports no image at all.
        /// </summary>
        internal static string? ResolveMessagePropertyName(string? message, string? explicitProperty)
            => string.IsNullOrWhiteSpace(explicitProperty) ? GetMessagePropertyName(message) : explicitProperty!.Trim();

        /// <summary>
        /// Why Dataverse rejects an image registered against <paramref name="explicitProperty"/> on
        /// <paramref name="message"/>, or null when it accepts it. An empty property means the message's own and is
        /// always accepted here (the message and stage rules still apply). Besides the message's own property, only
        /// <c>SubordinateId</c> on Merge is accepted.
        /// </summary>
        internal static string? GetMessagePropertyRejectionReason(string? message, string? explicitProperty)
        {
            if (string.IsNullOrWhiteSpace(explicitProperty))
            {
                return null;
            }

            string property = explicitProperty!.Trim();
            if (string.Equals(property, GetMessagePropertyName(message), System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(property, SubordinateId, System.StringComparison.OrdinalIgnoreCase))
            {
                return (message ?? string.Empty).Trim().ToLowerInvariant() == "merge"
                    ? null
                    : "only a Merge step can capture an image of its subordinate record (SubordinateId)";
            }

            return $"Vela registers images on this message against '{GetMessagePropertyName(message) ?? "nothing"}' only, not '{property}'";
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
