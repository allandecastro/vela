using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.Context;
using Vela.Manifest;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // #439: harness behaviours that turned a real failure into a passing test. One regression test (or more) per row.
    public sealed class SilentFalseGreenTests
    {
        private static readonly Guid AccountId = new Guid("43900000-0000-0000-0000-000000000001");
        private static readonly Guid ContactId = new Guid("43900000-0000-0000-0000-0000000000c1");

        private static InMemoryOrganizationService SeededAccount()
        {
            return new InMemoryOrganizationService().Seed(new Entity("account", AccountId)
            {
                ["name"] = "Acme",
                ["vela_since"] = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                ["revenue"] = new Money(10.5m),
                ["primarycontactid"] = new EntityReference("contact", ContactId),
                ["donotemail"] = true,
                ["numberofemployees"] = 42,
            });
        }

        private static int Fetch(InMemoryOrganizationService svc, string attribute, string op, params string[] values)
        {
            string body = values.Length == 1
                ? $"<condition attribute='{attribute}' operator='{op}' value='{values[0]}' />"
                : $"<condition attribute='{attribute}' operator='{op}'>{string.Concat(values.Select(v => $"<value>{v}</value>"))}</condition>";
            string fetch = $"<fetch><entity name='account'><attribute name='name' /><filter>{body}</filter></entity></fetch>";
            return svc.RetrieveMultiple(new FetchExpression(fetch)).Entities.Count;
        }

        // ---- Row 1: FetchXML condition values compare as the column's type ----------------------------------

        [Theory]
        [InlineData("vela_since", "2026-10-09", "2026-10-10")]
        [InlineData("vela_since", "2026-10-09T00:00:00Z", "2026-10-10")]
        [InlineData("revenue", "10.50", "11")]
        [InlineData("primarycontactid", "{43900000-0000-0000-0000-0000000000C1}", "00000000-0000-0000-0000-000000000000")]
        [InlineData("donotemail", "1", "0")]
        [InlineData("numberofemployees", "42", "7")]
        public void Fetch_eq_reads_the_value_as_the_column_type(string attribute, string value, string other)
        {
            InMemoryOrganizationService svc = SeededAccount();

            // The raw text used to be compared with the stored value's string form ("10/09/2026 00:00:00", "10.5",
            // the bare guid, "True"), so eq matched nothing and ne matched everything.
            Assert.Equal(1, Fetch(svc, attribute, "eq", value));
            Assert.Equal(0, Fetch(svc, attribute, "ne", value));
            Assert.Equal(1, Fetch(svc, attribute, "in", other, value));
            Assert.Equal(0, Fetch(svc, attribute, "not-in", other, value));
            Assert.Equal(0, Fetch(svc, attribute, "eq", other));
        }

        [Fact]
        public void Fetch_value_that_is_not_valid_for_the_column_type_fails_instead_of_matching_nothing()
        {
            InMemoryOrganizationService svc = SeededAccount();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Fetch(svc, "primarycontactid", "ne", "not-a-guid"));
            Assert.Contains("'not-a-guid' is not a valid Guid", ex.Message);
        }

        [Fact]
        public void QueryByAttribute_text_value_is_read_as_the_column_type()
        {
            InMemoryOrganizationService svc = SeededAccount();
            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("name") };
            query.AddAttributeValue("revenue", "10.50");

            Assert.Single(svc.RetrieveMultiple(query).Entities);
        }

        // ---- Row 2: ordered operators parse with the invariant culture and handle Guid ----------------------

        [Fact]
        public void Fetch_ordered_operators_do_not_depend_on_the_machine_culture()
        {
            InMemoryOrganizationService svc = SeededAccount();
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                // fr-FR reads "10.4" as 104 and "10/08/2026" as 10 August: the comparison must not change.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");

                Assert.Equal(1, Fetch(svc, "revenue", "gt", "10.4"));
                Assert.Equal(0, Fetch(svc, "revenue", "gt", "10.6"));
                Assert.Equal(1, Fetch(svc, "revenue", "between", "10.4", "10.6"));
                Assert.Equal(1, Fetch(svc, "vela_since", "gt", "2026-10-08"));
                Assert.Equal(0, Fetch(svc, "vela_since", "lt", "2026-10-08"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public void Fetch_ordered_operator_on_a_lookup_compares_guids_instead_of_throwing()
        {
            InMemoryOrganizationService svc = SeededAccount();

            // string -> Guid through Convert.ChangeType threw InvalidCastException.
            Assert.Equal(1, Fetch(svc, "primarycontactid", "gt", Guid.Empty.ToString()));
            Assert.Equal(1, Fetch(svc, "primarycontactid", "le", ContactId.ToString("B")));
        }

        // ---- Row 3: a store-side rejection of a modelled message fails the operation -----------------------

        [Fact]
        public void Merge_on_a_table_that_cannot_merge_fails_and_skips_the_post_step()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RecordMergePost>();
            Guid a = pipeline.Service.Create(new Entity("vela_widget"));
            Guid b = pipeline.Service.Create(new Entity("vela_widget"));

            // The store's rejection used to be caught as "message not modelled": an empty success, post steps fired.
            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => pipeline.Service.Execute(new MergeRequest
            {
                Target = new EntityReference("vela_widget", a),
                SubordinateId = b,
                PerformParentingChecks = false,
            }));

            Assert.Contains("Merge is not supported for 'vela_widget'", ex.Message);
            Assert.Empty(pipeline.GetAll("task"));
        }

        [Fact]
        public void A_message_the_store_does_not_model_still_fires_its_steps_around_a_no_op()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OnAccountAddedToQueue>();

            pipeline.Service.Execute(new AddToQueueRequest
            {
                Target = new EntityReference("account", AccountId),
                DestinationQueueId = Guid.NewGuid(),
            });

            Assert.Single(pipeline.GetAll("task"));
        }

        // ---- Row 4: a registration that adds nothing fails the setup ----------------------------------------

        [Fact]
        public void Register_a_type_with_no_step_and_no_custom_api_throws()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginPipeline.Empty().Register<PlainPluginWithoutSteps>());

            Assert.Contains(typeof(PlainPluginWithoutSteps).FullName, ex.Message);
            Assert.Contains("declares no step and no Custom API", ex.Message);
        }

        [Fact]
        public void FromAssembly_throws_when_a_manifest_type_cannot_be_resolved()
        {
            // A manifest entry naming a type that is not in the assembly (renamed or moved plugin) used to be dropped.
            FrameworkManifest manifest = new FrameworkManifest
            {
                PluginSteps = new[]
                {
                    new StepDefinition { PluginTypeName = "Vela.Testing.Tests.RenamedPlugin", Message = "Update", Stage = PluginStages.PostOperation },
                },
                CustomApis = new[] { new Vela.CustomApi.CustomApiDefinition { UniqueName = "vela_Moved", PluginTypeName = "Vela.Testing.Tests.MovedApi" } },
            };
            MethodInfo fromManifest = typeof(PluginPipeline).GetMethod("FromManifest", BindingFlags.NonPublic | BindingFlags.Static)!;

            TargetInvocationException wrapped = Assert.Throws<TargetInvocationException>(
                () => fromManifest.Invoke(null, new object[] { typeof(SilentFalseGreenTests).Assembly, manifest }));

            InvalidOperationException ex = Assert.IsType<InvalidOperationException>(wrapped.InnerException);
            Assert.Contains("could not register 2 definition(s)", ex.Message);
            Assert.Contains("step Update/PostOperation of 'Vela.Testing.Tests.RenamedPlugin'", ex.Message);
            Assert.Contains("Custom API 'vela_Moved' of 'Vela.Testing.Tests.MovedApi'", ex.Message);
        }

        // ---- Row 5: unset hooks throw (covered in DoublesCoverageTests); the builder default service too -----

        [Fact]
        public void Context_builder_default_service_fails_at_an_unplanned_call()
        {
            IServiceProvider provider = new PluginContextBuilder<FakeAccount>().BuildServiceProvider();
            IOrganizationService service = ((IOrganizationServiceFactory)provider.GetService(typeof(IOrganizationServiceFactory))!)
                .CreateOrganizationService(Guid.NewGuid());

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => service.Retrieve("account", AccountId, new ColumnSet(true)));
            Assert.Contains("OnRetrieve", ex.Message);
        }

        // ---- Row 7: WithStrictSecurity protects Delete without WithSecurity ----------------------------------

        [Fact]
        public void Strict_security_without_a_model_fails_a_delete()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(new Entity("account", AccountId) { ["name"] = "Acme" })
                .WithStrictSecurity();

            Assert.Throws<InvalidOperationException>(() => crm.Delete("account", AccountId));
            Assert.Single(crm.GetAll("account"));
        }

        // A Merge step on any table: its presence routes Merge through the generic message path.
        public sealed class RecordMergePost : EntityPluginBase<FakeAccount>
        {
            public RecordMergePost(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMerge().PostOperation().Sync().Handle(OnMerged);

            private void OnMerged(IMergePostContext<FakeAccount> ctx)
                => ctx.SystemService.Create(new Entity("task") { ["subject"] = "merged" });
        }

        // A raw IPlugin: constructible, but it declares nothing the pipeline could fire.
        public sealed class PlainPluginWithoutSteps : IPlugin
        {
            public PlainPluginWithoutSteps(string unsecure, string secure) { }

            public void Execute(IServiceProvider serviceProvider) { }
        }
    }
}
