namespace Vela.Cli;

/// <summary>Demo code for the Integrations project of <c>vela init --samples</c>.</summary>
internal static partial class ScaffoldTemplates
{
    // ---- Plugin code (Integrations project) ------------------------------

    public static string ContactSyncPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Contact.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;
    using {{prefix}}.Shared.Services;

    /// <summary>
    /// Single-step integration plugin: runs asynchronously after a contact is created. Demonstrates
    /// dependency injection of the cross-table <see cref="IGreetingService"/> in a second plugin package.
    /// </summary>
    // partial: the handler's context (OnContactCreatedContext) and its typed image projection are generated as
    // nested types inside this class (VELA014).
    public sealed partial class ContactSyncPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Contact>(unsecureConfig, secureConfig)
    {
        /// <inheritdoc />
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            services.AddScoped<IGreetingService>(_ => new GreetingService());
        }

        /// <inheritdoc />
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Async()
                .FilterOn(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .WithDescription("Runs after a contact is created.")
                .WithPostImage(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .Handle(OnContactCreated);
        }

        private void OnContactCreated(OnContactCreatedContext ctx)
        {
            Contact target = ctx.GetTarget<Contact>();
            IGreetingService greetings = ctx.GetRequiredService<IGreetingService>();
            string greeting = greetings.BuildGreeting(target.FirstName);

            // Trace the id, not the greeting: it carries the contact's first name, and the plugin trace log is
            // readable by administrators.
            ctx.Trace("Built a {0}-character greeting for contact {1}.", greeting.Length, ctx.Response.id);
        }
    }
}
""";

    public static string AccountSyncRouterPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Multi-step integration router: one class handling create and update of accounts, each bound to
    /// a dedicated handler method.
    /// </summary>
    // partial: the typed image projections are generated as nested types inside this class (VELA014).
    public sealed partial class AccountSyncRouterPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        /// <inheritdoc />
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Async()
                .FilterOn(a => a.Name)
                .WithDescription("Pushes a newly created account downstream.")
                .WithPostImage(a => a.Name)
                .Handle(OnAccountCreated);

            RegisterStep().OnUpdate()
                .PostOperation()
                .Async()
                .FilterOn(a => a.Name, a => a.Telephone1)
                .WithDescription("Pushes account changes downstream.")
                .WithPreImage(a => a.Name, a => a.Telephone1)
                .Handle(OnAccountChanged);
        }

        // Generated typed step context (OnAccountCreatedContext): ctx.PostImage is the captured post-image (Name only).
        private void OnAccountCreated(OnAccountCreatedContext ctx)
        {
            var postImage = ctx.PostImage;
            ctx.Trace("Sync: account {0} created.", ctx.Response.id);
        }

        // Generated typed step context (OnAccountChangedContext): ctx.PreImage is the captured pre-image (Name + Telephone1).
        private void OnAccountChanged(OnAccountChangedContext ctx)
        {
            var preImage = ctx.PreImage;
            ctx.Trace("Sync: account {0} changed.", ctx.Request.Target.Id);
        }
    }
}
""";

    public static string ContactExportCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Custom API <b>bound</b> to the <c>contact</c> table: exports a contact to an external system.</summary>
    public sealed class ContactExportCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<ContactExportRequest, ContactExportResponse>(unsecureConfig, secureConfig)
    {
        /// <summary>The unique name, shared by the registration, the request, the response and the tests.</summary>
        public const string UniqueName = "{{apiPrefix ?? ApiPrefix(prefix)}}_ContactExport";

        /// <inheritdoc />
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi(UniqueName)
                .DisplayName("Export Contact")
                .Description("Exports the bound contact to an external system.")
                .AsAction()
                .BoundTo<Contact>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.ContactId))
                .Response(r => r.Add(x => x.ExternalId));
        }

        /// <inheritdoc />
        protected override ContactExportResponse Execute(ILocalPluginContext ctx, ContactExportRequest request)
        {
            ctx.Trace("Exporting contact {0}", request.ContactId);

            // Replace with real integration logic.
            return new ContactExportResponse { ExternalId = request.ContactId.ToString() };
        }
    }

    /// <summary>Request parameters for <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportRequest : CustomApiRequest
    {
        /// <summary>Creates the request.</summary>
        public ContactExportRequest() : base(ContactExportCustomApi.UniqueName)
        {
        }

        /// <summary>The bound contact's id.</summary>
        public Guid ContactId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportResponse : CustomApiResponse
    {
        /// <summary>Creates the response.</summary>
        public ContactExportResponse() : base(ContactExportCustomApi.UniqueName)
        {
        }

        /// <summary>The contact's id in the external system.</summary>
        public string ExternalId
        {
            get => Get<string>();
            set => Set(value);
        }
    }
}
""";

    public static string AccountReconcileCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Custom API <b>bound</b> to the <c>account</c> table: reconciles the account with an external system.</summary>
    public sealed class AccountReconcileCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<AccountReconcileRequest, AccountReconcileResponse>(unsecureConfig, secureConfig)
    {
        /// <summary>The unique name, shared by the registration, the request, the response and the tests.</summary>
        public const string UniqueName = "{{apiPrefix ?? ApiPrefix(prefix)}}_AccountReconcile";

        /// <inheritdoc />
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi(UniqueName)
                .DisplayName("Reconcile Account")
                .Description("Reconciles the bound account with an external system.")
                .AsAction()
                .BoundTo<Account>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.AccountId))
                .Response(r => r.Add(x => x.Status));
        }

        /// <inheritdoc />
        protected override AccountReconcileResponse Execute(ILocalPluginContext ctx, AccountReconcileRequest request)
        {
            ctx.Trace("Reconciling account {0}", request.AccountId);

            // Replace with real integration logic.
            return new AccountReconcileResponse { Status = "Reconciled" };
        }
    }

    /// <summary>Request parameters for <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileRequest : CustomApiRequest
    {
        /// <summary>Creates the request.</summary>
        public AccountReconcileRequest() : base(AccountReconcileCustomApi.UniqueName)
        {
        }

        /// <summary>The bound account's id.</summary>
        public Guid AccountId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileResponse : CustomApiResponse
    {
        /// <summary>Creates the response.</summary>
        public AccountReconcileResponse() : base(AccountReconcileCustomApi.UniqueName)
        {
        }

        /// <summary>The outcome of the reconciliation.</summary>
        public string Status
        {
            get => Get<string>();
            set => Set(value);
        }
    }
}
""";

    public static string IntegrationHeartbeatCustomApi(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.GlobalCustomApis
{
    using Vela.Context;
    using Vela.CustomApi;

    /// <summary><b>Global</b> (unbound) Custom API that reports integration health.</summary>
    public sealed class IntegrationHeartbeatCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        /// <summary>The unique name, shared by the registration and the tests.</summary>
        public const string UniqueName = "{{apiPrefix ?? ApiPrefix(prefix)}}_IntegrationHeartbeat";

        /// <inheritdoc />
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi(UniqueName, Handle)
                .DisplayName("Integration Heartbeat")
                .Description("Returns the integration status.")
                .AsFunction()
                .ResponseParameter<string>("Status");
        }

        private void Handle(ILocalPluginContext ctx)
        {
            ctx.SetOutputParameter("Status", "healthy");
        }
    }
}
""";
}
