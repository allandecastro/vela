using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // A-59: reads go through the pipeline like Dataverse. Retrieve/RetrieveMultiple steps fire, a Pre step that
    // changes the ColumnSet/Query changes what is read, a Post step that changes the output changes what the caller
    // gets, and a returned record has the platform's shape (primary key, lookup names, choice labels).
    public sealed class PipelineRetrieveTests
    {
        private static PluginPipeline Seeded(params Type[] plugins)
        {
            PluginPipeline pipeline = PluginPipeline.Empty();
            foreach (Type plugin in plugins)
            {
                pipeline.Register(plugin);
            }

            return pipeline.Seed(
                new Entity(RdBook.LogicalNameValue, BookA) { ["vela_name"] = "Alpha", ["vela_secret"] = "s1", ["vela_genre"] = new OptionSetValue(1) },
                new Entity(RdBook.LogicalNameValue, BookB) { ["vela_name"] = "Beta", ["vela_secret"] = "s2" });
        }

        private static readonly Guid BookA = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid BookB = new Guid("00000000-0000-0000-0000-00000000000b");

        [Fact]
        public void A_Retrieve_PreOperation_step_that_narrows_the_ColumnSet_changes_what_is_read()
        {
            PluginPipeline pipeline = Seeded(typeof(RdRetrieveHidesSecret));

            Entity book = pipeline.Service.Retrieve(RdBook.LogicalNameValue, BookA, new ColumnSet(true));

            Assert.Equal("Alpha", book["vela_name"]);
            Assert.False(book.Contains("vela_secret"));
            Assert.Contains(pipeline.ExecutionLog, e => e.Message == "Retrieve");
        }

        [Fact]
        public void A_Retrieve_PostOperation_step_that_changes_the_output_changes_what_the_caller_gets()
        {
            PluginPipeline pipeline = Seeded(typeof(RdRetrieveStamps));

            Entity viaShortcut = pipeline.Service.Retrieve(RdBook.LogicalNameValue, BookA, new ColumnSet("vela_name"));
            RetrieveResponse viaRequest = (RetrieveResponse)pipeline.Service.Execute(new RetrieveRequest
            {
                Target = new EntityReference(RdBook.LogicalNameValue, BookB),
                ColumnSet = new ColumnSet("vela_name"),
            });

            Assert.Equal("stamped", viaShortcut["vela_note"]);
            Assert.Equal("stamped", viaRequest.Entity["vela_note"]);
        }

        [Fact]
        public void A_RetrieveMultiple_PreOperation_step_that_replaces_the_Query_changes_the_rows()
        {
            PluginPipeline pipeline = Seeded(typeof(RdRetrieveMultipleOnlyAlpha));

            EntityCollection rows = pipeline.Service.RetrieveMultiple(new QueryExpression(RdBook.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name") });
            RetrieveMultipleResponse response = (RetrieveMultipleResponse)pipeline.Service.Execute(
                new RetrieveMultipleRequest { Query = new FetchExpression("<fetch><entity name='vela_rdbook'><attribute name='vela_name' /></entity></fetch>") });

            Assert.Equal(new[] { "Alpha" }, rows.Entities.Select(e => (string)e["vela_name"]).ToArray());
            Assert.Equal(new[] { "Alpha" }, response.EntityCollection.Entities.Select(e => (string)e["vela_name"]).ToArray());
            // The Post step saw the collection under the platform key and could change it.
            Assert.Equal("seen", rows.Entities[0]["vela_note"]);
        }

        [Fact]
        public void Reads_of_another_table_do_not_fire_the_steps()
        {
            PluginPipeline pipeline = Seeded(typeof(RdRetrieveHidesSecret), typeof(RdRetrieveMultipleOnlyAlpha));
            Guid other = Guid.NewGuid();
            pipeline.Seed(new Entity("account", other) { ["name"] = "Zava" });

            pipeline.Service.Retrieve("account", other, new ColumnSet(true));
            pipeline.Service.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) });

            Assert.Empty(pipeline.ExecutionLog);
        }

        [Fact]
        public void A_retrieved_record_carries_its_primary_key_even_when_not_selected()
        {
            PluginPipeline pipeline = Seeded();

            Entity book = pipeline.Service.Retrieve(RdBook.LogicalNameValue, BookA, new ColumnSet("vela_name"));
            Entity row = pipeline.Service.RetrieveMultiple(new QueryExpression(RdBook.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name") }).Entities.First();

            Assert.Equal(BookA, book["vela_rdbookid"]);
            Assert.Equal(row.Id, row["vela_rdbookid"]);
        }

        [Fact]
        public void An_activity_uses_activityid_and_metadata_overrides_the_primary_key_name()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For("vela_custom").PrimaryId("vela_customkey").Build());
            Guid task = crm.Create(new Entity("task") { ["subject"] = "Call" });
            Guid custom = crm.Create(new Entity("vela_custom") { ["vela_name"] = "x" });

            Assert.Equal(task, crm.Retrieve("task", task, new ColumnSet("subject"))["activityid"]);
            Assert.Equal(custom, crm.Retrieve("vela_custom", custom, new ColumnSet("vela_name"))["vela_customkey"]);
        }

        [Fact]
        public void A_lookup_carries_the_referenced_records_name_and_a_choice_its_label()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithEntityMetadata(FakeEntityMetadata.For(RdBook.LogicalNameValue).Picklist("vela_genre", (1, "Fiction"), (2, "Essay")).Build());
            Guid contact = crm.Create(new Entity("contact") { ["fullname"] = "Ada Lovelace" });
            Guid account = crm.Create(new Entity("account") { ["name"] = "Zava" });
            Guid book = crm.Create(new Entity(RdBook.LogicalNameValue)
            {
                ["vela_author"] = new EntityReference("contact", contact),
                ["vela_publisher"] = new EntityReference("account", account),
                ["vela_genre"] = new OptionSetValue(2),
            });

            Entity read = crm.Retrieve(RdBook.LogicalNameValue, book, new ColumnSet(true));
            Entity queried = crm.RetrieveMultiple(new QueryExpression(RdBook.LogicalNameValue) { ColumnSet = new ColumnSet(true) }).Entities.Single();

            foreach (Entity row in new[] { read, queried })
            {
                Assert.Equal("Ada Lovelace", row.GetAttributeValue<EntityReference>("vela_author").Name);
                Assert.Equal("Zava", row.GetAttributeValue<EntityReference>("vela_publisher").Name);
                Assert.Equal("Ada Lovelace", row.FormattedValues["vela_author"]);
                Assert.Equal("Essay", row.FormattedValues["vela_genre"]);
            }

            // The stored lookup is not modified by the read.
            Assert.Null(crm.GetAll(RdBook.LogicalNameValue).Single().GetAttributeValue<EntityReference>("vela_author").Name);
        }

        [Fact]
        public void Ordering_by_the_primary_key_sorts_the_rows()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(new Entity(RdBook.LogicalNameValue, BookB) { ["vela_name"] = "Beta" }, new Entity(RdBook.LogicalNameValue, BookA) { ["vela_name"] = "Alpha" });
            QueryExpression query = new QueryExpression(RdBook.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name") };
            query.AddOrder("vela_rdbookid", OrderType.Descending);

            Assert.Equal(new[] { BookB, BookA }, crm.RetrieveMultiple(query).Entities.Select(e => e.Id).ToArray());
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class RdBook : Entity
    {
        public const string LogicalNameValue = "vela_rdbook";

        public RdBook() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    public sealed class RdRetrieveHidesSecret : EntityPluginBase<RdBook>
    {
        public RdRetrieveHidesSecret(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnRetrieve().PreOperation().Sync().Handle(ctx =>
                ctx.PluginExecutionContext.InputParameters["ColumnSet"] = new ColumnSet("vela_name"));
    }

    public sealed class RdRetrieveStamps : EntityPluginBase<RdBook>
    {
        public RdRetrieveStamps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnRetrieve().PostOperation().Sync().Handle(ctx =>
            {
                Entity read = (Entity)ctx.PluginExecutionContext.OutputParameters["BusinessEntity"];
                Entity replaced = new Entity(read.LogicalName, read.Id) { ["vela_name"] = read["vela_name"], ["vela_note"] = "stamped" };
                ctx.PluginExecutionContext.OutputParameters["BusinessEntity"] = replaced;
            });
    }

    public sealed class RdRetrieveMultipleOnlyAlpha : EntityPluginBase<RdBook>
    {
        public RdRetrieveMultipleOnlyAlpha(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnRetrieveMultiple().PreOperation().Sync().Handle(ctx =>
            {
                QueryExpression narrowed = new QueryExpression(RdBook.LogicalNameValue) { ColumnSet = new ColumnSet("vela_name") };
                narrowed.Criteria.AddCondition("vela_name", ConditionOperator.Equal, "Alpha");
                ctx.PluginExecutionContext.InputParameters["Query"] = narrowed;
            });
            RegisterStep().OnRetrieveMultiple().PostOperation().Sync().Handle(ctx =>
            {
                EntityCollection rows = (EntityCollection)ctx.PluginExecutionContext.OutputParameters["BusinessEntityCollection"];
                foreach (Entity row in rows.Entities)
                {
                    row["vela_note"] = "seen";
                }
            });
        }
    }
}
