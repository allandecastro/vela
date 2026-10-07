namespace Vela.Cli;

/// <summary>Unit tests for the <c>vela init --samples</c> demo code, run with Vela.Testing.</summary>
internal static partial class ScaffoldTemplates
{
    // ---- Sample tests (Plugins.Tests / Integrations.Tests) ----------------

    public static string AccountRelatedContactsPluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Steps;
    using Vela.Testing;
    using Microsoft.Xrm.Sdk;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests <see cref="AccountRelatedContactsPlugin"/> against an in-memory Dataverse: seeds an account with two
    /// contacts and asserts the plugin's typed query (<c>Query.For&lt;Contact&gt;()...Build()</c>, a QueryExpression)
    /// counts them; the query runs through the harness's in-memory query engine.
    /// </summary>
    public sealed class AccountRelatedContactsPluginTests
    {
        [Fact]
        public void Counts_the_related_contacts_via_the_typed_query_builder()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Account { Id = accountId, Name = "Zava" },
                new Contact { Id = Guid.NewGuid(), FirstName = "Jane", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) },
                new Contact { Id = Guid.NewGuid(), FirstName = "John", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) });

            PluginContextBuilder<Account> builder = PluginContext.For<Account>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync()
                .WithPrimaryEntity(Account.EntityLogicalNameValue, accountId)
                .WithTarget(new Account { Id = accountId, Name = "Zava (renamed)" })
                .WithOrganizationService(crm);

            builder.Execute(new AccountRelatedContactsPlugin(string.Empty, string.Empty));

            Assert.Contains("has 2 related contact", builder.TraceCapture.Output);
        }
    }
}
""";

    public static string SecurityExampleTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Security
{
    using System;
    using System.ServiceModel;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Testing;
    using Vela.Testing.Security;
    using Xunit;

    /// <summary>
    /// Example: exercising the harness's opt-in SECURITY MODEL. Security is OFF by default - with no
    /// <c>WithSecurity(...)</c> call the in-memory service grants full access (which is what the other tests rely
    /// on). Declare business units, named roles and users with <c>WithSecurity(...)</c>, pick who is acting with
    /// <c>WithCurrentUser(...)</c>, and the service then enforces table privileges on Create/Retrieve/Update/Delete
    /// and trims <c>RetrieveMultiple</c> to the records the caller may read - exactly like Dataverse. A denied
    /// operation throws <see cref="FaultException{OrganizationServiceFault}"/>, the same fault a live plugin sees.
    /// <c>Seed(...)</c> writes rows straight to the store (bypassing the checks) so you can arrange the world, then
    /// act as a specific user to assert what they can and cannot do.
    /// </summary>
    public sealed class SecurityExampleTests
    {
        private static readonly Guid SalesBusinessUnit = new Guid("00000000-0000-0000-0000-0000000b0001");
        private static readonly Guid Salesperson = new Guid("00000000-0000-0000-0000-0000000a0001");
        private static readonly Guid OutsideUser = new Guid("00000000-0000-0000-0000-0000000a0002");

        // A small scenario: one business unit and a "salesrep" role that can create/read/write accounts across the
        // whole organization. Salesperson holds it; OutsideUser holds no role at all (so: no access to anything).
        //
        // Roles are declared inline (by name) here so the example is self-contained. To mirror your REAL environment,
        // generate a dependency-light `Roles` class from its security roles with `vela models --roles` (or
        // `--solution-roles`) into `{{prefix}}.Shared/Models/Generated`, then configure the harness with the actual
        // privileges via `Role(Roles.SalesrepName, Roles.SalesrepId, Roles.Salesrep)` instead of the string name below.
        private static SecurityConfiguration Scenario(SecurityConfiguration security) => security
            .BusinessUnit(SalesBusinessUnit)
            .Role("salesrep", role => role.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write))
            .User(Salesperson, SalesBusinessUnit, "salesrep")
            .User(OutsideUser, SalesBusinessUnit);

        [Fact]
        public void A_user_with_the_role_can_create_and_read()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Salesperson)
                .WithSecurity(security => Scenario(security));

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" }); // salesrep grants Create
            Entity account = crm.Retrieve("account", id, new ColumnSet(true));  // ...and Read

            Assert.Equal("Zava", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_user_without_any_privilege_is_denied()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(OutsideUser)
                .WithSecurity(security => Scenario(security))
                .Seed(new Entity("account", id) { ["name"] = "Zava" }); // Seed bypasses security to arrange the row

            // OutsideUser has no Read privilege on account, so Retrieve throws the platform fault a plugin would see.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Retrieve("account", id, new ColumnSet(true)));
        }

        [Fact]
        public void RetrieveMultiple_returns_only_the_records_the_user_can_read()
        {
            Guid mine = Guid.NewGuid();
            Guid theirs = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Salesperson)
                .WithSecurity(security => security
                    .BusinessUnit(SalesBusinessUnit)
                    // User-level Read: the caller sees only records they own, not the whole table.
                    .Role("ownrecords", role => role.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Salesperson, SalesBusinessUnit, "ownrecords")
                    .User(OutsideUser, SalesBusinessUnit, "ownrecords"))
                .Seed(
                    new Entity("account", mine) { ["name"] = "Mine", ["ownerid"] = new EntityReference("systemuser", Salesperson) },
                    new Entity("account", theirs) { ["name"] = "Theirs", ["ownerid"] = new EntityReference("systemuser", OutsideUser) });

            EntityCollection result = crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) });

            Entity onlyMine = Assert.Single(result.Entities); // User-level Read trims the record owned by someone else
            Assert.Equal(mine, onlyMine.Id);
        }
    }
}
""";

    public static string AccountPostCreatePluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.Plugins
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests <see cref="AccountPostCreatePlugin"/> with <c>Vela.Testing</c>: an in-memory Dataverse plus a
    /// fake execution context - no connection, no mocking library. Plugins are constructed with their
    /// <c>(unsecureConfig, secureConfig)</c> constructor and run via <c>.Execute(plugin)</c>.
    /// </summary>
    public sealed class AccountPostCreatePluginTests
    {
        [Fact]
        public void Schedules_A_Follow_Up_Task_For_The_New_Account()
        {
            // Arrange: an in-memory Dataverse the plugin's injected IOrganizationService writes to.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid accountId = Guid.NewGuid();
            Account account = new Account { Id = accountId, Name = "Zava" };

            // Act: run the plugin. The trigger (Create/PostOperation/Synchronous) is auto-derived from the
            // plugin's own single registered step, so there is no need to restate OnCreate().Stage(...).Mode(...).
            PluginContext.For<Account>()
                .WithPrimaryEntity(Account.EntityLogicalNameValue, accountId)
                .WithTarget(account)
                .WithPostImage(account)
                .WithOrganizationService(crm)
                .Execute(new AccountPostCreatePlugin(string.Empty, string.Empty));

            // Assert: retrieve the created task and check its fields (not just the count).
            EntityCollection tasks = crm.RetrieveMultiple(
                new QueryExpression("task") { ColumnSet = new ColumnSet(true) });
            Entity task = Assert.Single(tasks.Entities);
            Assert.Equal("Follow up with Zava", task.GetAttributeValue<string>("subject"));

            EntityReference regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
            Assert.Equal("account", regarding.LogicalName);
            Assert.Equal(accountId, regarding.Id);
        }
    }
}
""";

    public static string ContactRouterPluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Contact.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Steps;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests the multi-step "router" <see cref="ContactRouterPlugin"/>. Because a router registers several
    /// steps, the trigger (message/stage/mode) must be stated explicitly so the right handler runs. Asserts
    /// on the emitted traces via <c>TraceCapture</c>.
    /// </summary>
    public sealed class ContactRouterPluginTests
    {
        [Fact]
        public void OnCreate_Flags_A_Possible_Duplicate_With_The_Same_Email()
        {
            // Seed an existing contact that shares the email of the one being created.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid existingId = crm.Create(new Contact
            {
                FirstName = "Jane",
                LastName = "Doe",
                EMailAddress1 = "jane@zava.com",
            });

            Guid newContactId = Guid.NewGuid();
            Contact created = new Contact
            {
                Id = newContactId,
                FirstName = "Janet",
                LastName = "Doe",
                EMailAddress1 = "jane@zava.com",
            };

            PluginContextBuilder<Contact> builder = PluginContext.For<Contact>()
                .OnCreate().Stage(PluginStages.PostOperation).Sync()
                .WithPrimaryEntity(Contact.EntityLogicalNameValue, newContactId)
                .WithTarget(created)
                .WithPostImage(created)
                .WithOrganizationService(crm);

            builder.Execute(new ContactRouterPlugin(string.Empty, string.Empty));

            Assert.Contains("Contact created: Janet Doe", builder.TraceCapture.Output);
            Assert.Contains("Possible duplicate of contact " + existingId, builder.TraceCapture.Output);
        }
    }
}
""";

    public static string AccountEstimateValueCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.CustomApis;

    /// <summary>
    /// Tests the typed, account-bound Custom API <see cref="AccountEstimateValueCustomApi"/>. The framework
    /// maps input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class AccountEstimateValueCustomApiTests
    {
        [Fact]
        public void Returns_An_Estimated_Value_For_The_Bound_Account()
        {
            AccountEstimateValueCustomApi api = new AccountEstimateValueCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_AccountEstimateValue").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal(1000m, context.PluginExecutionContext.OutputParameters["EstimatedValue"]);
        }
    }
}
""";

    public static string ContactValidateCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Contact.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.CustomApis;

    /// <summary>
    /// Tests the typed, contact-bound Custom API <see cref="ContactValidateCustomApi"/>. The framework maps
    /// input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class ContactValidateCustomApiTests
    {
        [Fact]
        public void Reports_The_Bound_Contact_As_Valid()
        {
            ContactValidateCustomApi api = new ContactValidateCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_ContactValidate").Build();
            context.PluginExecutionContext.InputParameters["ContactId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal(true, context.PluginExecutionContext.OutputParameters["IsValid"]);
        }
    }
}
""";

    public static string PingCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.GlobalCustomApis
{
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.GlobalCustomApis;

    /// <summary>
    /// Tests the global (unbound), lightweight Custom API <see cref="PingCustomApi"/>: input parameters are
    /// read with <c>ctx.GetInputParameter</c> and results written with <c>ctx.SetOutputParameter</c>.
    /// </summary>
    public sealed class PingCustomApiTests
    {
        [Fact]
        public void Returns_Pong_With_The_Supplied_Message()
        {
            PingCustomApi api = new PingCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_Ping").Build();
            context.PluginExecutionContext.InputParameters["Message"] = "hello";

            api.Execute(context.ServiceProvider);

            Assert.Equal("pong: hello", context.PluginExecutionContext.OutputParameters["Pong"]);
        }
    }
}
""";

    public static string AccountReconcileCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Account.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Account.CustomApis;

    /// <summary>Tests the typed, account-bound Custom API <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileCustomApiTests
    {
        [Fact]
        public void Returns_A_Reconciled_Status_For_The_Bound_Account()
        {
            AccountReconcileCustomApi api = new AccountReconcileCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_AccountReconcile").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal("Reconciled", context.PluginExecutionContext.OutputParameters["Status"]);
        }
    }
}
""";

    public static string ContactExportCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Contact.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Contact.CustomApis;

    /// <summary>Tests the typed, contact-bound Custom API <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportCustomApiTests
    {
        [Fact]
        public void Returns_The_Contact_Id_As_External_Id()
        {
            ContactExportCustomApi api = new ContactExportCustomApi(string.Empty, string.Empty);
            Guid contactId = Guid.NewGuid();
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_ContactExport").Build();
            context.PluginExecutionContext.InputParameters["ContactId"] = contactId;

            api.Execute(context.ServiceProvider);

            Assert.Equal(contactId.ToString(), context.PluginExecutionContext.OutputParameters["ExternalId"]);
        }
    }
}
""";

    public static string IntegrationHeartbeatCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.GlobalCustomApis
{
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.GlobalCustomApis;

    /// <summary>Tests the global (unbound), lightweight Custom API <see cref="IntegrationHeartbeatCustomApi"/>.</summary>
    public sealed class IntegrationHeartbeatCustomApiTests
    {
        [Fact]
        public void Reports_A_Healthy_Status()
        {
            IntegrationHeartbeatCustomApi api = new IntegrationHeartbeatCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{apiPrefix ?? ApiPrefix(prefix)}}_IntegrationHeartbeat").Build();

            api.Execute(context.ServiceProvider);

            Assert.Equal("healthy", context.PluginExecutionContext.OutputParameters["Status"]);
        }
    }
}
""";
}
