namespace Vela.Cli;

/// <summary>Demo code for <c>vela init --samples</c>: shared models/services, sample plugins and Custom APIs, and the console app.</summary>
internal static partial class ScaffoldTemplates
{
    // ---- Shared project samples ------------------------------------------

    public static string AccountModel(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Models.Generated
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    /// <summary>Minimal early-bound model for the <c>account</c> table.</summary>
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Account : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public Account()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("accountid")]
        public Guid? AccountId
        {
            get => GetAttributeValue<Guid?>("accountid");
            set => SetAttributeValue("accountid", value);
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [AttributeLogicalName("telephone1")]
        public string Telephone1
        {
            get => GetAttributeValue<string>("telephone1");
            set => SetAttributeValue("telephone1", value);
        }

        [AttributeLogicalName("revenue")]
        public Money Revenue
        {
            get => GetAttributeValue<Money>("revenue");
            set => SetAttributeValue("revenue", value);
        }
    }
}
""";

    public static string ContactModel(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Models.Generated
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    /// <summary>Minimal early-bound model for the <c>contact</c> table.</summary>
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Contact : Entity
    {
        public const string EntityLogicalNameValue = "contact";

        public Contact()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("contactid")]
        public Guid? ContactId
        {
            get => GetAttributeValue<Guid?>("contactid");
            set => SetAttributeValue("contactid", value);
        }

        [AttributeLogicalName("firstname")]
        public string FirstName
        {
            get => GetAttributeValue<string>("firstname");
            set => SetAttributeValue("firstname", value);
        }

        [AttributeLogicalName("lastname")]
        public string LastName
        {
            get => GetAttributeValue<string>("lastname");
            set => SetAttributeValue("lastname", value);
        }

        [AttributeLogicalName("emailaddress1")]
        public string EMailAddress1
        {
            get => GetAttributeValue<string>("emailaddress1");
            set => SetAttributeValue("emailaddress1", value);
        }

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference ParentCustomerId
        {
            get => GetAttributeValue<EntityReference>("parentcustomerid");
            set => SetAttributeValue("parentcustomerid", value);
        }
    }
}
""";

    public static string GreetingServiceInterface(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Services
{
    /// <summary>Example shared domain service resolved through the plugin dependency container.</summary>
    public interface IGreetingService
    {
        string BuildGreeting(string name);
    }
}
""";

    public static string GreetingService(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Services
{
    using System;

    /// <inheritdoc />
    public sealed class GreetingService : IGreetingService
    {
        public string BuildGreeting(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? "Hello from {{prefix}}!"
                : "Hello, " + name + ", from {{prefix}}!";
        }
    }
}
""";

    // ---- Table-scoped services (Plugins project) -------------------------

    public static string AccountFollowUpService(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Services
{
    using System;
    using Microsoft.Xrm.Sdk;

    /// <summary>
    /// Account-scoped domain service: schedules follow-up activities for an account. Unlike the
    /// cross-table <c>IGreetingService</c> in the Shared project, this service lives with the Account
    /// table because only account plugins use it.
    /// </summary>
    public interface IAccountFollowUpService
    {
        Guid CreateFollowUpTask(Guid accountId, string accountName);
    }

    /// <inheritdoc />
    public sealed class AccountFollowUpService(IOrganizationService service) : IAccountFollowUpService
    {
        // Business rule: chase a newly created account one week later.
        private const int FollowUpDays = 7;

        private readonly IOrganizationService _service = service;

        public Guid CreateFollowUpTask(Guid accountId, string accountName)
        {
            Entity task = new("task")
            {
                ["subject"] = "Follow up with " + accountName,
                ["description"] = "Auto-created " + FollowUpDays + " days after the account was created.",
                ["scheduledend"] = DateTime.UtcNow.AddDays(FollowUpDays),
                ["regardingobjectid"] = new EntityReference("account", accountId),
            };

            return _service.Create(task);
        }
    }
}
""";

    public static string ContactDuplicateService(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Contact.Services
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Querying;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Contact-scoped domain service: finds a possible duplicate contact by email. A second
    /// table-scoped service (the first being <c>IAccountFollowUpService</c>) that, in contrast, <b>reads</b>
    /// from Dataverse rather than writing.
    /// </summary>
    public interface IContactDuplicateService
    {
        Guid? FindDuplicateByEmail(string email, Guid excludeContactId);
    }

    /// <inheritdoc />
    public sealed class ContactDuplicateService(IOrganizationService service) : IContactDuplicateService
    {
        private readonly IOrganizationService _service = service;

        public Guid? FindDuplicateByEmail(string email, Guid excludeContactId)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            // Typed query over the early-bound Contact: table and column names are checked at compile time
            // (a typo won't build). No columns are selected - only the matching row's Id is needed.
            EntityCollection matches = _service.RetrieveMultiple(
                Query.For<Contact>()
                    .Where(c => c.EMailAddress1, ConditionOperator.Equal, email)
                    .Where(c => c.ContactId, ConditionOperator.NotEqual, excludeContactId)
                    .Top(1)
                    .Build());

            return matches.Entities.Count > 0 ? matches.Entities[0].Id : (Guid?)null;
        }
    }
}
""";

    // ---- Plugin code (Plugins project) -----------------------------------

    public static string AccountRelatedContactsPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using System;
    using Vela.Caching;
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using Vela.Querying;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Single-step plugin: after an account is updated, counts the contacts that reference it. The query is
    /// authored with the TYPED query builder <c>Query.For&lt;Contact&gt;()</c> - table and column names are checked
    /// at compile time (a typo won't build). The builder emits either FetchXML (<c>ToFetchXml()</c>) or a
    /// <c>QueryExpression</c> (<c>Build()</c>); this sample runs the <c>QueryExpression</c>. The count is served
    /// through the built-in <c>IPluginCacheService</c> (read-through, 5-minute TTL) so a hot account is not
    /// re-queried on every update.
    /// </summary>
    public sealed partial class AccountRelatedContactsPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithDescription("Counts the contacts related to an account after it is updated.")
                .Handle(OnAccountUpdated);
        }

        private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
        {
            Account target = ctx.GetTarget<Account>();

            // Typed query -> a strongly-typed QueryExpression (use ToFetchXml() instead for FetchXML).
            QueryExpression query = Query.For<Contact>()
                .Select(c => c.FirstName, c => c.LastName)
                .Where(c => c.ParentCustomerId, ConditionOperator.Equal, target.Id)
                .Build();

            // Read-through cache with a short TTL: the RetrieveMultiple only runs on a cache miss (or after the
            // entry expires). Key by account so entries never collide. IPluginCacheService is a built-in,
            // process-level service - no registration needed.
            IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();
            int count = cache.GetOrCreate(
                $"account:related-contacts:{target.Id}",
                TimeSpan.FromMinutes(5),
                () => ctx.Service.RetrieveMultiple(query).Entities.Count);

            ctx.Trace("Account {0} has {1} related contact(s).", target.Id, count);
        }
    }
}
""";

    public static string AccountPostCreatePlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using Microsoft.Xrm.Sdk;
    using {{prefix}}.Plugins.Account.Services;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// After an account is created, schedules a follow-up task a week out. Uses a typed post-stage handler
    /// (<see cref="ICreatePostContext{TEntity}"/>) so <c>ctx.Response.id</c> and <c>ctx.Request.Target</c> are strongly
    /// typed, and shows dependency injection via <see cref="ConfigureServices"/> plus a table-scoped service
    /// (<see cref="IAccountFollowUpService"/>) resolved with <c>ctx.GetRequiredService&lt;T&gt;()</c>.
    /// </summary>
    // partial: the typed image projection is generated as a nested type inside this class (VELA014).
    public sealed partial class AccountPostCreatePlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            // The default registrations already expose the initiating-user IOrganizationService; hand it
            // to the table-scoped service.
            services.AddScoped<IAccountFollowUpService>(r => new AccountFollowUpService(r.GetRequiredService<IOrganizationService>()));
        }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithDescription("Schedules a 7-day follow-up task after an account is created.")
                .WithPostImage(a => a.Name)
                .Handle(OnAccountCreated);
        }

        // Typed handler: because the step is PostOperation, ctx is an ICreatePostContext<Account>, so ctx.Response
        // (the CreateResponse) and ctx.Request.Target (the created Account) are strongly typed. It is still an
        // ILocalPluginContext, so images and services work as usual.
        // The parameter is the generated typed step context (OnAccountCreatedContext, a subclass of
        // ICreatePostContext<Account>): ctx.PostImage is the captured post-image, typed to the captured columns only.
        private void OnAccountCreated(OnAccountCreatedContext ctx)
        {
            var postImage = ctx.PostImage;
            IAccountFollowUpService followUp = ctx.GetRequiredService<IAccountFollowUpService>();

            Guid taskId = followUp.CreateFollowUpTask(ctx.Response.id, postImage.Name);
            ctx.Trace("Created follow-up task {0} for account '{1}'.", taskId, postImage.Name);
        }
    }
}
""";

    public static string AccountMessageReference(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// REFERENCE / CHEAT-SHEET - one handler per typed message Vela models, showing the strongly-typed context
    /// each receives. Every step is registered <c>.Disabled()</c>, so this class is safe to deploy: nothing fires
    /// until you enable a step. Copy the handler for the message you need into a real plugin, then delete this
    /// file. Each handler infers its request/response from the message (no cast); the primary record is read via
    /// <c>ctx.Request.Target</c> (Create / Update, an early-bound entity) / <c>ctx.Request.Targets</c> (the
    /// SDK-typed <c>EntityReference</c> / <c>EntityCollection</c> for the rest).
    /// </summary>
    public sealed partial class AccountMessageReference(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            // Create / Update carry a full entity Target; both require at least one filtering attribute.
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnCreate);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnUpdate);

            // Delete carries an EntityReference Target.
            RegisterStep().OnDelete().PreOperation().Sync().Disabled().Handle(OnDelete);

            // Bulk messages carry a Targets collection; they also require a filtering attribute.
            RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnCreateMultiple);
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnUpdateMultiple);

            // Read messages.
            RegisterStep().OnRetrieve().PostOperation().Sync().Disabled().Handle(OnRetrieve);
            RegisterStep().OnRetrieveMultiple().PostOperation().Sync().Disabled().Handle(OnRetrieveMultiple);

            // Relationship messages.
            RegisterStep().OnAssociate().PostOperation().Sync().Disabled().Handle(OnAssociate);
            RegisterStep().OnDisassociate().PostOperation().Sync().Disabled().Handle(OnDisassociate);

            // Merge and sharing (access) messages.
            RegisterStep().OnMerge().PostOperation().Sync().Disabled().Handle(OnMerge);
            RegisterStep().OnGrantAccess().PostOperation().Sync().Disabled().Handle(OnGrantAccess);
            RegisterStep().OnModifyAccess().PostOperation().Sync().Disabled().Handle(OnModifyAccess);
            RegisterStep().OnRevokeAccess().PostOperation().Sync().Disabled().Handle(OnRevokeAccess);

            // Ownership reassignment (entity-bound).
            RegisterStep().OnAssign().PostOperation().Sync().Disabled().Handle(OnAssign);

            // Sharing reads (also valid on account).
            RegisterStep().OnRetrievePrincipalAccess().PostOperation().Sync().Disabled().Handle(OnRetrievePrincipalAccess);
            RegisterStep().OnRetrieveSharedPrincipalsAndAccess().PostOperation().Sync().Disabled().Handle(OnRetrieveSharedPrincipalsAndAccess);

            // Many more messages are modelled but belong to other tables, so they are not shown here on account:
            // e.g. QualifyLead (lead); WinOpportunity / LoseOpportunity / Generate*/Convert* (opportunity / quote /
            // order); AddToQueue / RouteTo (queue items); SendEmail / SendEmailFromTemplate (email); AddMembersTeam /
            // AddListMembersList (team / marketing list); CloseIncident (case); CancelContract / CloneContract
            // (contract). Register them the same way from the matching EntityPluginBase<T> with RegisterStep().On<Message>().
        }

        // ctx.Request.Target is the early-bound Account; ctx.Response.id is the new record id (Post step).
        private void OnCreate(ICreatePostContext<Account> ctx)
            => ctx.Trace("Create {0} -> id {1}", ctx.Request.Target.Name, ctx.Response.id);

        // ctx.Request.Target carries the columns that changed on this update.
        private void OnUpdate(IUpdatePostContext<Account> ctx)
            => ctx.Trace("Update {0}", ctx.Request.Target.Id);

        // Delete carries an EntityReference, exposed as ctx.Request.Target. This step is PreOperation, so the
        // handler receives the PRE context (ctx.Request but no ctx.Response - the record is not deleted yet).
        private void OnDelete(IDeletePreContext<Account> ctx)
            => ctx.Trace("Delete {0}", ctx.Request.Target.Id);

        // Bulk create: ctx.Request.Targets is the whole batch; ctx.Response.Ids are the new ids.
        private void OnCreateMultiple(ICreateMultiplePostContext<Account> ctx)
            => ctx.Trace("CreateMultiple of {0}", ctx.Request.Targets.Entities.Count);

        private void OnUpdateMultiple(IUpdateMultiplePostContext<Account> ctx)
            => ctx.Trace("UpdateMultiple of {0}", ctx.Request.Targets.Entities.Count);

        // Retrieve: ctx.Request.Target is the record read; ctx.Request.ColumnSet the requested columns.
        private void OnRetrieve(IRetrievePostContext<Account> ctx)
            => ctx.Trace("Retrieve {0} ({1} columns)", ctx.Request.Target.Id, ctx.Request.ColumnSet.Columns.Count);

        // RetrieveMultiple has no primary Target; the query is on ctx.Request.Query.
        private void OnRetrieveMultiple(IRetrieveMultiplePostContext<Account> ctx)
            => ctx.Trace("RetrieveMultiple, query type {0}", ctx.Request.Query.GetType().Name);

        // Associate / Disassociate: ctx.Request.Target plus ctx.Request.Relationship / RelatedEntities.
        private void OnAssociate(IAssociatePostContext<Account> ctx)
            => ctx.Trace("Associate {0} via {1}", ctx.Request.Target.Id, ctx.Request.Relationship.SchemaName);

        private void OnDisassociate(IDisassociatePostContext<Account> ctx)
            => ctx.Trace("Disassociate {0} via {1}", ctx.Request.Target.Id, ctx.Request.Relationship.SchemaName);

        // Merge: ctx.Request.Target is the master, ctx.Request.SubordinateId the record merged into it.
        private void OnMerge(IMergePostContext<Account> ctx)
            => ctx.Trace("Merge {0} into {1}", ctx.Request.SubordinateId, ctx.Request.Target.Id);

        // Sharing: ctx.Request.PrincipalAccess (grant/modify) and ctx.Request.Revokee (revoke).
        private void OnGrantAccess(IGrantAccessPostContext<Account> ctx)
            => ctx.Trace("Grant on {0} to {1}", ctx.Request.Target.Id, ctx.Request.PrincipalAccess.Principal.Id);

        private void OnModifyAccess(IModifyAccessPostContext<Account> ctx)
            => ctx.Trace("Modify access on {0}", ctx.Request.Target.Id);

        private void OnRevokeAccess(IRevokeAccessPostContext<Account> ctx)
            => ctx.Trace("Revoke on {0} from {1}", ctx.Request.Target.Id, ctx.Request.Revokee.Id);

        // Assign: ctx.Request.Target is the reassigned record; ctx.Request.Assignee the new owner.
        private void OnAssign(IAssignPostContext<Account> ctx)
            => ctx.Trace("Assign {0} to {1}", ctx.Request.Target.Id, ctx.Request.Assignee.Id);

        // Sharing reads: ctx.Response.AccessRights (one principal) / ctx.Response.PrincipalAccesses (all shares).
        private void OnRetrievePrincipalAccess(IRetrievePrincipalAccessPostContext<Account> ctx)
            => ctx.Trace("Access of {0} on {1}: {2}", ctx.Request.Principal.Id, ctx.Request.Target.Id, ctx.Response.AccessRights);

        private void OnRetrieveSharedPrincipalsAndAccess(IRetrieveSharedPrincipalsAndAccessPostContext<Account> ctx)
            => ctx.Trace("{0} share(s) on {1}", ctx.Response.PrincipalAccesses.Length, ctx.Request.Target.Id);
    }
}
""";

    public static string ContactRouterPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Contact.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using Microsoft.Xrm.Sdk;
    using {{prefix}}.Plugins.Contact.Services;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Multi-step "router" plugin: registers several steps in one class and binds each to a named
    /// handler method. Every step in a multi-step plugin must end with <c>Handle(...)</c>. Also shows a
    /// router consuming a table-scoped service (<see cref="IContactDuplicateService"/>) via DI.
    /// </summary>
    // partial: the typed image projections are generated as nested types inside this class (VELA014).
    public sealed partial class ContactRouterPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Contact>(unsecureConfig, secureConfig)
    {
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            services.AddScoped<IContactDuplicateService>(r => new ContactDuplicateService(r.GetRequiredService<IOrganizationService>()));
        }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .WithDescription("Handles contact creation.")
                .WithPostImage(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .Handle(OnCreatePost);

            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(c => c.LastName)
                .WithDescription("Handles contact last-name changes.")
                .WithPreImage(c => c.LastName)
                .Handle(OnUpdatePost);
        }

        // Each handler receives the message-typed context: ICreateContext exposes a CreateRequest, IUpdateContext
        // an UpdateRequest (in a Post step, a matching Response too), each strongly typed to this message. The
        // record is available already typed as ctx.Request.Target - no cast, no type argument. The context is still an
        // ILocalPluginContext, so images, services and tracing remain available.
        // Generated typed step context (OnCreatePostContext): ctx.PostImage is the captured post-image, typed to the
        // captured columns only (reading an uncaptured column is a compile error, not a silent null).
        private void OnCreatePost(OnCreatePostContext ctx)
        {
            var postImage = ctx.PostImage;
            ctx.Trace("Contact created: {0} {1}", postImage.FirstName, postImage.LastName);

            IContactDuplicateService duplicates = ctx.GetRequiredService<IContactDuplicateService>();
            Guid? duplicateId = duplicates.FindDuplicateByEmail(postImage.EMailAddress1, ctx.Request.Target.Id);
            if (duplicateId.HasValue)
            {
                ctx.Trace("Possible duplicate of contact {0} (same email).", duplicateId.Value);
            }
        }

        // Generated typed step context (OnUpdatePostContext): ctx.PreImage is the captured pre-image (LastName only).
        private void OnUpdatePost(OnUpdatePostContext ctx)
        {
            var preImage = ctx.PreImage;
            ctx.Trace("Contact last name changed from '{0}' to '{1}'", preImage.LastName, ctx.Request.Target.LastName);
        }
    }
}
""";

    public static string AccountEstimateValueCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Custom API <b>bound</b> to the <c>account</c> table. The request/response derive from
    /// <see cref="TypedRequest"/>/<see cref="TypedResponse"/> so the same type serves authoring,
    /// calling (<c>service.Execute(new EstimateValueRequest { ... }).ToResponse&lt;EstimateValueResponse&gt;()</c>),
    /// and reacting.
    /// </summary>
    public sealed class AccountEstimateValueCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<EstimateValueRequest, EstimateValueResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{apiPrefix ?? ApiPrefix(prefix)}}_AccountEstimateValue")
                .DisplayName("Estimate Account Value")
                .Description("Estimates a value for the bound account.")
                .AsAction()
                .BoundTo<Account>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r
                    .Add(x => x.AccountId)
                    .Add(x => x.AsOfDate, optional: true, "Date to value against; defaults to today."))
                .Response(r => r.Add(x => x.EstimatedValue));
        }

        protected override EstimateValueResponse Execute(ILocalPluginContext ctx, EstimateValueRequest request)
        {
            ctx.Trace("Estimating value for account {0}", request.AccountId);

            // Replace with real logic.
            return new EstimateValueResponse { EstimatedValue = 1000m };
        }
    }

    /// <summary>Request parameters for <see cref="AccountEstimateValueCustomApi"/>.</summary>
    public sealed class EstimateValueRequest : CustomApiRequest
    {
        public EstimateValueRequest() : base("{{apiPrefix ?? ApiPrefix(prefix)}}_AccountEstimateValue")
        {
        }

        public Guid AccountId
        {
            get => Get<Guid>();
            set => Set(value);
        }

        // Optional request parameter (isoptional = true in Dataverse). Only request
        // parameters can be optional; response properties are always present.
        public DateTime AsOfDate
        {
            get => Get<DateTime>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="AccountEstimateValueCustomApi"/>.</summary>
    public sealed class EstimateValueResponse : CustomApiResponse
    {
        public EstimateValueResponse() : base("{{apiPrefix ?? ApiPrefix(prefix)}}_AccountEstimateValue")
        {
        }

        public decimal EstimatedValue
        {
            get => Get<decimal>();
            set => Set(value);
        }
    }
}
""";

    public static string ContactValidateCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Custom API <b>bound</b> to the <c>contact</c> table. The request/response derive from
    /// <see cref="TypedRequest"/>/<see cref="TypedResponse"/> so the same type serves authoring,
    /// calling (<c>service.Execute(new ContactValidateRequest { ... }).ToResponse&lt;ContactValidateResponse&gt;()</c>),
    /// and reacting.
    /// </summary>
    public sealed class ContactValidateCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<ContactValidateRequest, ContactValidateResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{apiPrefix ?? ApiPrefix(prefix)}}_ContactValidate")
                .DisplayName("Validate Contact")
                .Description("Validates the bound contact's email address.")
                .AsAction()
                .BoundTo<Contact>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.ContactId))
                .Response(r => r.Add(x => x.IsValid));
        }

        protected override ContactValidateResponse Execute(ILocalPluginContext ctx, ContactValidateRequest request)
        {
            ctx.Trace("Validating contact {0}", request.ContactId);

            // Replace with real validation logic.
            return new ContactValidateResponse { IsValid = true };
        }
    }

    /// <summary>Request parameters for <see cref="ContactValidateCustomApi"/>.</summary>
    public sealed class ContactValidateRequest : CustomApiRequest
    {
        public ContactValidateRequest() : base("{{apiPrefix ?? ApiPrefix(prefix)}}_ContactValidate")
        {
        }

        public Guid ContactId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="ContactValidateCustomApi"/>.</summary>
    public sealed class ContactValidateResponse : CustomApiResponse
    {
        public ContactValidateResponse() : base("{{apiPrefix ?? ApiPrefix(prefix)}}_ContactValidate")
        {
        }

        public bool IsValid
        {
            get => Get<bool>();
            set => Set(value);
        }
    }
}
""";

    public static string PingCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.GlobalCustomApis
{
    using Vela.Context;
    using Vela.CustomApi;

    /// <summary>
    /// <b>Global</b> (unbound) Custom API using the lightweight style - no dedicated request/response
    /// models, parameters declared inline.
    /// </summary>
    public sealed class PingCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{apiPrefix ?? ApiPrefix(prefix)}}_Ping", Handle)
                .DisplayName("Ping")
                .Description("Returns a pong message; useful for connectivity checks.")
                .AsAction()
                .RequestParameter<string>("Message", optional: true)
                .ResponseParameter<string>("Pong");
        }

        private void Handle(ILocalPluginContext ctx)
        {
            string message = ctx.GetInputParameter<string>("Message");
            ctx.SetOutputParameter("Pong", string.IsNullOrEmpty(message) ? "pong" : "pong: " + message);
        }
    }
}
""";

    // ---- ConsoleApp project ----------------------------------------------

    public static string ConsoleProgram(string prefix) =>
$$"""
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using {{prefix}}.Shared.Models.Generated;

// Interactive (browser) sign-in to Dataverse. Pass the environment URL as the first argument,
// or enter it when prompted; a browser window opens for Microsoft login.
string? url = args.Length > 0 ? args[0] : null;
if (string.IsNullOrWhiteSpace(url))
{
    Console.Write("Dataverse environment URL (https://org.crm.dynamics.com): ");
    url = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(url))
{
    Console.Error.WriteLine("An environment URL is required.");
    return 1;
}

// Well-known Microsoft public client registered for interactive Dataverse sign-in.
// The redirect URI must be loopback: MSAL's system-browser flow rejects non-loopback redirects.
const string publicAppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
const string redirectUri = "http://localhost";
string connectionString =
    $"AuthType=OAuth;Url={url};AppId={publicAppId};RedirectUri={redirectUri};LoginPrompt=Auto";

using var service = new ServiceClient(connectionString);
if (!service.IsReady)
{
    Console.Error.WriteLine($"Failed to connect: {service.LastError}");
    return 1;
}

var whoAmI = (WhoAmIResponse)service.Execute(new WhoAmIRequest());
Console.WriteLine($"Connected as user {whoAmI.UserId}.");

// Retrieve the five most recent accounts using the shared early-bound model.
var query = new QueryExpression(Account.EntityLogicalNameValue)
{
    ColumnSet = new ColumnSet("name"),
    TopCount = 5,
};

EntityCollection result = service.RetrieveMultiple(query);
Console.WriteLine($"Found {result.Entities.Count} account(s):");
foreach (Account account in result.Entities.Select(e => e.ToEntity<Account>()))
{
    Console.WriteLine($"  - {account.Name}");
}

return 0;
""";
}
