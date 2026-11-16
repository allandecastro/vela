using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // ctx.Sides() on Associate / Disassociate: the same link gives the same split whichever end the caller passed as
    // Target. Associate/Disassociate are global messages, so a contact plugin's handler runs with an account Target
    // when the caller associates from the account.
    public sealed class RelationshipSidesTests
    {
        private static readonly Guid AccountId = Guid.NewGuid();
        private static readonly Guid ContactA = Guid.NewGuid();
        private static readonly Guid ContactB = Guid.NewGuid();

        private static PluginPipeline Pipeline()
        {
            SidesContactPlugin.Seen.Clear();
            return PluginPipeline.Empty()
                .Register<SidesContactPlugin>()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .WithManyToMany("contact_peers", "contact_peer", "contact", "contactidone", "contact", "contactidtwo")
                .Seed(new Entity("account", AccountId), new Entity("contact", ContactA), new Entity("contact", ContactB));
        }

        private static EntityReferenceCollection Refs(params EntityReference[] refs) => new EntityReferenceCollection(refs);

        private static string Describe(IEnumerable<EntityReference> refs) =>
            string.Join(",", refs.Select(r => r.LogicalName + ":" + r.Id).OrderBy(s => s, StringComparer.Ordinal));

        [Fact]
        public void Associate_from_the_account_side_puts_the_contacts_on_this_side()
        {
            PluginPipeline pipeline = Pipeline();

            pipeline.Service.Associate("account", AccountId, new Relationship("account_contacts"),
                Refs(new EntityReference("contact", ContactA), new EntityReference("contact", ContactB)));

            RelationshipSides sides = Assert.Single(SidesContactPlugin.Seen).Sides;
            Assert.Equal("account_contacts", sides.Relationship!.SchemaName);
            Assert.Equal(Describe(new[] { new EntityReference("contact", ContactA), new EntityReference("contact", ContactB) }), Describe(sides.ThisSide));
            EntityReference other = Assert.Single(sides.OtherSide);
            Assert.Equal(("account", AccountId), (other.LogicalName, other.Id));
        }

        [Fact]
        public void Associate_from_either_side_gives_the_same_split()
        {
            PluginPipeline fromAccount = Pipeline();
            fromAccount.Service.Associate("account", AccountId, new Relationship("account_contacts"), Refs(new EntityReference("contact", ContactA)));
            RelationshipSides accountSide = Assert.Single(SidesContactPlugin.Seen).Sides;

            PluginPipeline fromContact = Pipeline();
            fromContact.Service.Associate("contact", ContactA, new Relationship("account_contacts"), Refs(new EntityReference("account", AccountId)));
            RelationshipSides contactSide = Assert.Single(SidesContactPlugin.Seen).Sides;

            Assert.Equal("contact:" + ContactA, Describe(accountSide.ThisSide));
            Assert.Equal(Describe(accountSide.ThisSide), Describe(contactSide.ThisSide));
            Assert.Equal("account:" + AccountId, Describe(accountSide.OtherSide));
            Assert.Equal(Describe(accountSide.OtherSide), Describe(contactSide.OtherSide));
        }

        [Fact]
        public void Disassociate_from_either_side_gives_the_same_split()
        {
            PluginPipeline fromAccount = Pipeline();
            fromAccount.Service.Associate("account", AccountId, new Relationship("account_contacts"), Refs(new EntityReference("contact", ContactA)));
            SidesContactPlugin.Seen.Clear();
            fromAccount.Service.Disassociate("account", AccountId, new Relationship("account_contacts"), Refs(new EntityReference("contact", ContactA)));
            (string message, RelationshipSides accountSide) = Assert.Single(SidesContactPlugin.Seen);
            Assert.Equal("Disassociate", message);

            PluginPipeline fromContact = Pipeline();
            fromContact.Service.Associate("account", AccountId, new Relationship("account_contacts"), Refs(new EntityReference("contact", ContactA)));
            SidesContactPlugin.Seen.Clear();
            fromContact.Service.Disassociate("contact", ContactA, new Relationship("account_contacts"), Refs(new EntityReference("account", AccountId)));
            RelationshipSides contactSide = Assert.Single(SidesContactPlugin.Seen).Sides;

            Assert.Equal("contact:" + ContactA, Describe(accountSide.ThisSide));
            Assert.Equal(Describe(accountSide.ThisSide), Describe(contactSide.ThisSide));
            Assert.Equal("account:" + AccountId, Describe(accountSide.OtherSide));
            Assert.Equal(Describe(accountSide.OtherSide), Describe(contactSide.OtherSide));
        }

        [Fact]
        public void Self_referential_relationship_puts_the_target_on_this_side()
        {
            PluginPipeline pipeline = Pipeline();

            pipeline.Service.Associate("contact", ContactA, new Relationship("contact_peers") { PrimaryEntityRole = EntityRole.Referencing }, Refs(new EntityReference("contact", ContactB)));

            RelationshipSides sides = Assert.Single(SidesContactPlugin.Seen).Sides;
            Assert.Equal("contact:" + ContactA, Describe(sides.ThisSide));
            Assert.Equal("contact:" + ContactB, Describe(sides.OtherSide));
        }
    }

    [EntityLogicalName("contact")]
    public sealed class SidesContact : Entity
    {
        public SidesContact()
            : base("contact")
        {
        }
    }

    public sealed class SidesContactPlugin : EntityPluginBase<SidesContact>
    {
        public SidesContactPlugin(string u, string s) : base(u, s) { }

        internal static List<(string Message, RelationshipSides Sides)> Seen { get; } = new List<(string, RelationshipSides)>();

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnAssociate().PostOperation().Sync()
                .ForRelationship("account_contacts", "contact_peers")
                .Handle(ctx => Seen.Add((ctx.MessageName, ctx.Sides())));
            RegisterStep().OnDisassociate().PostOperation().Sync()
                .ForRelationship("account_contacts", "contact_peers")
                .Handle(ctx => Seen.Add((ctx.MessageName, ctx.Sides())));
        }
    }
}
