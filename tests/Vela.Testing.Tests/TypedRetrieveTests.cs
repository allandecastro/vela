using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // service.Retrieve<T>(id, columns...) through the harness: it is a real Retrieve, so the Retrieve steps fire, a
    // missing record faults with the platform's code and the caller's Read access applies.
    public sealed class TypedRetrieveTests
    {
        private static readonly Guid Book = new Guid("00000000-0000-0000-0000-0000000000b1");
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000101");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000102");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000001b1");

        [Fact]
        public void Returns_the_typed_record_with_the_selected_columns_only()
        {
            InMemoryOrganizationService service = new InMemoryOrganizationService()
                .Seed(new Entity(RdBook.LogicalNameValue, Book) { ["vela_name"] = "Alpha", ["vela_secret"] = "s1" });

            RdBook book = service.Retrieve<RdBook>(Book, b => b.Name);

            Assert.Equal(Book, book.Id);
            Assert.Equal("Alpha", book.Name);
            Assert.False(book.Contains("vela_secret"));
        }

        [Fact]
        public void Fires_the_Retrieve_steps_and_returns_what_they_produce()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<RdRetrieveStamps>()
                .Seed(new Entity(RdBook.LogicalNameValue, Book) { ["vela_name"] = "Alpha" });

            RdBook book = pipeline.Service.Retrieve<RdBook>(Book, b => b.Name);

            Assert.Contains(pipeline.ExecutionLog, e => e.Message == "Retrieve");
            Assert.Equal("stamped", book["vela_note"]);
            Assert.Equal("Alpha", book.Name);
        }

        [Fact]
        public void A_missing_record_faults_as_object_does_not_exist()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Retrieve<RdBook>(Guid.NewGuid(), b => b.Name));

            Assert.Equal(unchecked((int)0x80040217), fault.Detail.ErrorCode);
        }

        [Fact]
        public void A_record_the_caller_cannot_read_faults()
        {
            Guid aliceBook = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("reader", r => r.Grant(RdBook.LogicalNameValue, AccessLevel.User, Privilege.Read))
                    .User(Alice, SalesBu, "reader"))
                .Seed(OwnedBy(Book, Bob), OwnedBy(aliceBook, Alice));

            Assert.Equal("Mine", pipeline.Service.Retrieve<RdBook>(aliceBook, b => b.Name).Name);
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => pipeline.Service.Retrieve<RdBook>(Book, b => b.Name));
        }

        private static Entity OwnedBy(Guid id, Guid owner) =>
            new Entity(RdBook.LogicalNameValue, id)
            {
                ["vela_name"] = owner == Alice ? "Mine" : "Theirs",
                ["ownerid"] = new EntityReference("systemuser", owner),
                ["owningbusinessunit"] = new EntityReference("businessunit", SalesBu),
            };
    }
}
