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
    using Vela.Testing;
    using Microsoft.Xrm.Sdk;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests <see cref="AccountRelatedContactsPlugin"/> through <see cref="PluginPipeline"/>, which runs the step
    /// registration the way Dataverse does. The first test asserts the count returned by the handler's aggregate
    /// FetchXML query (the harness parses the emitted FetchXML, so the string is exercised end to end). The second
    /// checks the step's filter: change <c>FilterOn(a =&gt; a.Name)</c> to the wrong column and it fails.
    /// </summary>
    public sealed class AccountRelatedContactsPluginTests
    {
        [Fact]
        public void Counts_the_related_contacts_with_an_aggregate_fetchxml_query()
        {
            // Empty().Register<T>() scopes the pipeline to this plugin (FromAssemblyOf<T>() takes them all). Seed
            // writes the starting rows without firing any step.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountRelatedContactsPlugin>();
            Guid accountId = Guid.NewGuid();
            pipeline.Seed(
                new Account { Id = accountId, Name = "Zava" },
                new Contact { Id = Guid.NewGuid(), FirstName = "Jane", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) },
                new Contact { Id = Guid.NewGuid(), FirstName = "John", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) },
                new Contact { Id = Guid.NewGuid(), FirstName = "Ada" }); // no parent account: not counted

            pipeline.Service.Update(new Account { Id = accountId, Name = "Zava (renamed)" });

            Assert.Contains("has 2 related contact", pipeline.Trace.Output);
        }

        [Fact]
        public void Runs_only_when_the_account_name_changes()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountRelatedContactsPlugin>();
            Guid accountId = pipeline.Service.Create(new Account { Name = "Zava" });

            pipeline.Service.Update(new Account { Id = accountId, Telephone1 = "555-0100" });
            Assert.DoesNotContain(pipeline.ExecutionLog, e => e.PluginTypeName.EndsWith(nameof(AccountRelatedContactsPlugin)));

            pipeline.Service.Update(new Account { Id = accountId, Name = "Zava (renamed)" });
            PipelineExecutionEntry run = Assert.Single(pipeline.ExecutionLog, e => e.PluginTypeName.EndsWith(nameof(AccountRelatedContactsPlugin)));
            Assert.Equal("Update", run.Message);
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
            Entity account = crm.Retrieve("account", id, new ColumnSet("name"));  // ...and Read

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
                () => crm.Retrieve("account", id, new ColumnSet("name")));
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

            EntityCollection result = crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet("name") });

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
    /// Tests <see cref="AccountPostCreatePlugin"/> with <c>Vela.Testing</c>: <see cref="PluginPipeline"/> runs the
    /// plugin's registered steps against an in-memory Dataverse - no connection, no mocking library.
    /// </summary>
    public sealed class AccountPostCreatePluginTests
    {
        [Fact]
        public void Schedules_A_Follow_Up_Task_For_The_New_Account()
        {
            // Arrange: a pipeline scoped to this plugin.
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountPostCreatePlugin>();

            // Act: create the account. The Create PostOperation step fires with its post-image, as in Dataverse.
            Guid accountId = pipeline.Service.Create(new Account { Name = "Zava" });

            // Assert: retrieve the created task and check its fields (not just the count).
            EntityCollection tasks = pipeline.Service.RetrieveMultiple(
                new QueryExpression("task") { ColumnSet = new ColumnSet("subject", "regardingobjectid") });
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
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests the multi-step "router" <see cref="ContactRouterPlugin"/>. <see cref="PluginPipeline"/> picks the step
    /// the write triggers (a Create fires the Create step, not the Update one), so the test states no trigger.
    /// Asserts on the emitted traces via <c>pipeline.Trace</c>.
    /// </summary>
    public sealed class ContactRouterPluginTests
    {
        [Fact]
        public void OnCreate_Flags_A_Possible_Duplicate_With_The_Same_Email()
        {
            // An existing contact shares the email of the one being created.
            Guid existingId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ContactRouterPlugin>();
            pipeline.Seed(new Contact { Id = existingId, FirstName = "Jane", LastName = "Doe", EMailAddress1 = "jane@zava.com" });

            // PostOperation runs after the row is written, so the duplicate query also sees the new contact: its
            // "not this contact" condition keeps it from being reported as its own duplicate.
            Guid newContactId = pipeline.Service.Create(new Contact { FirstName = "Janet", LastName = "Doe", EMailAddress1 = "jane@zava.com" });

            Assert.Contains("Contact " + newContactId + " created.", pipeline.Trace.Output);
            Assert.Contains("Possible duplicate of contact " + existingId, pipeline.Trace.Output);
            Assert.DoesNotContain("Possible duplicate of contact " + newContactId, pipeline.Trace.Output);
        }
    }
}
""";

    public static string AccountEstimateValueCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.CustomApis;

    /// <summary>
    /// Tests the typed, account-bound Custom API <see cref="AccountEstimateValueCustomApi"/> through
    /// <see cref="PluginPipeline"/>, which validates the request against the definition as Dataverse does.
    /// The framework maps input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class AccountEstimateValueCustomApiTests
    {
        [Fact]
        public void Returns_An_Estimated_Value_For_The_Bound_Account()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountEstimateValueCustomApi>();
            Guid accountId = Guid.NewGuid();
            // A bound Custom API runs on a record of its table: the request's Target.
            EstimateValueRequest request = new EstimateValueRequest { AccountId = accountId };
            request["Target"] = new EntityReference("account", accountId);

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal(AccountEstimateValueCustomApi.PlaceholderEstimate, response.Results["EstimatedValue"]);
        }
    }
}
""";

    public static string ContactValidateCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.CustomApis;

    /// <summary>
    /// Tests the typed, contact-bound Custom API <see cref="ContactValidateCustomApi"/> through
    /// <see cref="PluginPipeline"/>, which validates the request against the definition as Dataverse does.
    /// The framework maps input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class ContactValidateCustomApiTests
    {
        [Fact]
        public void Reports_The_Bound_Contact_As_Valid()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ContactValidateCustomApi>();
            Guid contactId = Guid.NewGuid();
            // A bound Custom API runs on a record of its table: the request's Target.
            ContactValidateRequest request = new ContactValidateRequest { ContactId = contactId };
            request["Target"] = new EntityReference("contact", contactId);

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal(true, response.Results["IsValid"]);
        }
    }
}
""";

    public static string PingCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Plugins.Tests.GlobalCustomApis
{
    using Microsoft.Xrm.Sdk;
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
            PluginPipeline pipeline = PluginPipeline.Empty().Register<PingCustomApi>();

            OrganizationResponse response = pipeline.Service.Execute(new OrganizationRequest(PingCustomApi.UniqueName) { ["Message"] = "hello" });

            Assert.Equal("pong: hello", response.Results["Pong"]);
        }
    }
}
""";

    public static string AccountReconcileCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Account.CustomApis;

    /// <summary>
    /// Tests the typed, account-bound Custom API <see cref="AccountReconcileCustomApi"/>.
    /// </summary>
    public sealed class AccountReconcileCustomApiTests
    {
        [Fact]
        public void Returns_A_Reconciled_Status_For_The_Bound_Account()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountReconcileCustomApi>();
            Guid accountId = Guid.NewGuid();
            // A bound Custom API runs on a record of its table: the request's Target.
            AccountReconcileRequest request = new AccountReconcileRequest { AccountId = accountId };
            request["Target"] = new EntityReference("account", accountId);

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal("Reconciled", response.Results["Status"]);
        }
    }
}
""";

    public static string ContactExportCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Contact.CustomApis;

    /// <summary>
    /// Tests the typed, contact-bound Custom API <see cref="ContactExportCustomApi"/>.
    /// </summary>
    public sealed class ContactExportCustomApiTests
    {
        [Fact]
        public void Returns_The_Contact_Id_As_External_Id()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ContactExportCustomApi>();
            Guid contactId = Guid.NewGuid();
            // A bound Custom API runs on a record of its table: the request's Target.
            ContactExportRequest request = new ContactExportRequest { ContactId = contactId };
            request["Target"] = new EntityReference("contact", contactId);

            OrganizationResponse response = pipeline.Service.Execute(request);

            Assert.Equal(contactId.ToString(), response.Results["ExternalId"]);
        }
    }
}
""";

    public static string IntegrationHeartbeatCustomApiTests(string prefix, string? apiPrefix = null) =>
$$"""
namespace {{prefix}}.Integrations.Tests.GlobalCustomApis
{
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.GlobalCustomApis;

    /// <summary>
    /// Tests the global (unbound), lightweight Custom API <see cref="IntegrationHeartbeatCustomApi"/>.
    /// </summary>
    public sealed class IntegrationHeartbeatCustomApiTests
    {
        [Fact]
        public void Reports_A_Healthy_Status()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<IntegrationHeartbeatCustomApi>();

            OrganizationResponse response = pipeline.Service.Execute(new OrganizationRequest(IntegrationHeartbeatCustomApi.UniqueName));

            Assert.Equal("healthy", response.Results["Status"]);
        }
    }
}
""";
}
