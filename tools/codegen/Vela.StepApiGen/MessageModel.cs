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
internal sealed record Message(string Name, bool FilterOn = false, Images Images = Images.None)
{
    public bool HasPreImage(Stage stage) => Images is Images.Pre or Images.PreAndPost;

    public bool HasPostImage(Stage stage) => stage == Stage.PostOperation && Images is Images.Post or Images.PreAndPost;
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
        new("Associate"),
        new("Disassociate"),
        new("GrantAccess"),
        new("Assign", Images: Images.PreAndPost),
        new("RevokeAccess"),
        // Sales lifecycle
        new("QualifyLead"),
        new("WinOpportunity"),
        new("LoseOpportunity"),
        new("ConvertQuoteToSalesOrder"),
        new("ConvertSalesOrderToInvoice"),
        new("GenerateQuoteFromOpportunity"),
        new("GenerateSalesOrderFromOpportunity"),
        new("GenerateInvoiceFromOpportunity"),
        // Queues
        new("AddToQueue"),
        new("RemoveFromQueue"),
        new("PickFromQueue"),
        new("RouteTo"),
        new("ReleaseToQueue"),
        // Email
        new("SendEmail"),
        new("SendEmailFromTemplate"),
        new("DeliverIncomingEmail"),
        new("DeliverPromoteEmail"),
        // Membership
        new("AddMembersTeam"),
        new("RemoveMembersTeam"),
        new("AddListMembersList"),
        new("AddMemberList"),
        new("RemoveMemberList"),
        // Case and contract
        new("CloseIncident"),
        new("CancelContract"),
        new("CloneContract"),
        // Access queries, quotes, orders, products, access and merge
        new("RetrievePrincipalAccess"),
        new("RetrieveSharedPrincipalsAndAccess"),
        new("WinQuote"),
        new("ReviseQuote"),
        new("FulfillSalesOrder"),
        new("GetQuoteProductsFromOpportunity"),
        new("GetSalesOrderProductsFromOpportunity"),
        new("GetInvoiceProductsFromOpportunity"),
        new("ModifyAccess"),
        new("Merge"),
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
