namespace Vela.StepApiGen;

/// <summary>Which entity images a message supports. Pre-images are offered at every stage (PreValidation included),
/// post-images at PostOperation only (Dataverse rules).</summary>
internal enum Images
{
    None,
    Pre,
    Post,
    PreAndPost,
}

/// <summary>One modelled Dataverse message.</summary>
/// <param name="Name">Message name. Drives every generated identifier: <c>On{Name}()</c>, <c>I{Name}Stages</c>,
/// <c>I{Name}{Stage}Config</c>, the typed contexts <c>I{Name}PreContext</c> / <c>I{Name}PostContext</c> (hand-written
/// in <c>Context/MessageContexts.cs</c>) and the <c>PluginMessages.{Name}</c> constant.</param>
/// <param name="FilterOn">Whether the stage configs expose <c>FilterOn(...)</c> (filtering attributes).</param>
/// <param name="Images">Which images the stage configs expose (<c>WithPreImage</c> / <c>WithPostImage</c>).</param>
/// <param name="SubordinateImages">Whether the stage configs also expose images of the Merge subordinate record
/// (<c>WithSubordinatePreImage</c> / <c>WithSubordinatePostImage</c>, registered against <c>SubordinateId</c>), under the
/// same stage rules as <paramref name="Images"/>.</param>
/// <param name="Relationship">Whether the stage configs expose <c>ForRelationship(...)</c> (Associate / Disassociate).</param>
/// <param name="Entities">The tables Dataverse accepts a step on for this message (its sdkmessagefilters that allow
/// custom steps), or null when it is not limited to a known set. <c>Build()</c> rejects the trigger on any other
/// table. The step registers on the sdkmessage held by the <c>PluginMessages.{Name}</c> constant, which differs from
/// <c>Name</c> when the SDK request name is not the sdkmessage name (<c>OnWinQuote()</c> registers on <c>Win</c>).</param>
internal sealed record Message(string Name, bool FilterOn = false, Images Images = Images.None, bool SubordinateImages = false, bool Relationship = false, string[]? Entities = null)
{
    public bool HasPreImage(Stage stage) => Images is Images.Pre or Images.PreAndPost;

    public bool HasPostImage(Stage stage) => stage == Stage.PostOperation && Images is Images.Post or Images.PreAndPost;

    public bool HasSubordinatePreImage(Stage stage) => SubordinateImages && HasPreImage(stage);

    public bool HasSubordinatePostImage(Stage stage) => SubordinateImages && HasPostImage(stage);
}

/// <summary>
/// The declarative input of the generator: every message with a typed <c>On{Message}()</c> step builder.
/// Adding a modelled message = one line here (then run the generator). The order is the emission order.
/// See docs/contributing/extending-plugins.md for the other (hand-written) files a new message touches.
/// </summary>
internal static class MessageModel
{
    public static readonly IReadOnlyList<Message> Messages =
    [
        // CRUD and bulk
        new("Create", FilterOn: true, Images: Images.Post),
        new("Update", FilterOn: true, Images: Images.PreAndPost),
        new("CreateMultiple", FilterOn: true, Images: Images.Post),
        new("UpdateMultiple", FilterOn: true, Images: Images.PreAndPost),
        new("Delete", Images: Images.Pre),
        new("Retrieve"),
        new("RetrieveMultiple"),
        // Relationships, sharing and ownership
        new("Associate", Relationship: true),
        new("Disassociate", Relationship: true),
        new("GrantAccess"),
        new("Assign", Images: Images.PreAndPost),
        new("RevokeAccess"),
        // Sales lifecycle
        new("QualifyLead", Entities: ["lead"]),
        new("WinOpportunity", Entities: ["opportunity"]),
        new("LoseOpportunity", Entities: ["opportunity"]),
        new("ConvertSalesOrderToInvoice", Entities: ["salesorder"]),
        new("GenerateQuoteFromOpportunity", Entities: ["quote"]),
        // Queues
        new("AddToQueue", Entities: ["queueitem"]),
        new("RemoveFromQueue", Entities: ["queueitem"]),
        new("PickFromQueue", Entities: ["queueitem"]),
        new("RouteTo", Entities: ["queueitem"]),
        new("ReleaseToQueue", Entities: ["queueitem"]),
        // Email
        new("SendEmail", Entities: ["email"]),
        new("SendEmailFromTemplate", Entities: ["email"]),
        new("DeliverIncomingEmail", Entities: ["email"]),
        new("DeliverPromoteEmail", Entities: ["email"]),
        // Membership
        new("AddMembersTeam", Entities: ["team"]),
        new("RemoveMembersTeam", Entities: ["team"]),
        new("AddListMembersList", Entities: ["list"]),
        new("AddMemberList", Entities: ["list"]),
        new("RemoveMemberList", Entities: ["list"]),
        // Case and contract
        new("CloseIncident", Entities: ["incident"]),
        new("CancelContract", Entities: ["contract"]),
        new("CloneContract", Entities: ["contract"]),
        // Access queries, quotes, orders, products, access and merge
        new("RetrievePrincipalAccess"),
        new("RetrieveSharedPrincipalsAndAccess"),
        new("WinQuote", Entities: ["quote"]),
        new("ModifyAccess"),
        // Verified live: one Merge step takes images of the master (Target) and of the subordinate (SubordinateId).
        new("Merge", Images: Images.PreAndPost, SubordinateImages: true),
        // Marketing, pricing, goals, scheduling, email and product kits (#405). The tables are the sdkmessagefilters
        // that allow custom steps on a 9.2 environment. Reschedule runs on three tables (verified live);
        // UnlockSalesOrderPricing, UnlockInvoicePricing and RemoveProductFromKit on none (PluginMessages.GlobalMessages).
        new("AddItemCampaign", Entities: ["campaign"]),
        new("RemoveItemCampaign", Entities: ["campaign"]),
        new("Recalculate", Entities: ["goal"]),
        new("LockSalesOrderPricing", Entities: ["salesorder"]),
        new("UnlockSalesOrderPricing"),
        new("LockInvoicePricing", Entities: ["invoice"]),
        new("UnlockInvoicePricing"),
        new("Reschedule", Entities: ["appointment", "serviceappointment", "recurringappointmentmaster"]),
        new("ImmediateBook", Entities: ["appointment"]),
        new("SendTemplate", Entities: ["template"]),
        new("SendFax", Entities: ["fax"]),
        new("AddProductToKit", Entities: ["product"]),
        new("RemoveProductFromKit"),
    ];

    /// <summary>
    /// Member order of <c>IStepTrigger&lt;TEntity&gt;</c> in StepApi.cs. It matches <see cref="Messages"/> except
    /// that <c>OnRevokeAccess()</c> has always been declared before <c>OnAssign()</c> there. Interface member order
    /// has no semantic effect; the swap is kept only so the generated file matches the committed one byte for byte.
    /// </summary>
    public static IEnumerable<Message> StepTriggerOrder()
    {
        var list = Messages.ToList();
        int assign = list.FindIndex(m => m.Name == "Assign");
        int revoke = list.FindIndex(m => m.Name == "RevokeAccess");
        if (assign >= 0 && revoke == assign + 1)
        {
            (list[assign], list[revoke]) = (list[revoke], list[assign]);
        }
        return list;
    }
}

/// <summary>Pipeline stage of a step.</summary>
internal enum Stage
{
    PreValidation,
    PreOperation,
    PostOperation,
}

internal static class StageExtensions
{
    public static readonly Stage[] All = [Stage.PreValidation, Stage.PreOperation, Stage.PostOperation];

    /// <summary>Async is PostOperation-only in Dataverse, so pre-stages offer Sync() alone.</summary>
    public static string ModeInterface(this Stage stage) => stage == Stage.PostOperation ? "ISyncAsyncMode" : "ISyncMode";

    /// <summary>Typed contexts are split by stage: pre-stage handlers get <c>Pre</c>, PostOperation gets <c>Post</c>.</summary>
    public static string ContextSuffix(this Stage stage) => stage == Stage.PostOperation ? "Post" : "Pre";
}
