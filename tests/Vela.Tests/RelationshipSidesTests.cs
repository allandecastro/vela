using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // ctx.Sides() splits an Associate / Disassociate by the plugin's table, whichever end the caller passed as Target.
    public sealed class RelationshipSidesTests
    {
        private static readonly EntityReference Account = new EntityReference("account", Guid.NewGuid());
        private static readonly EntityReference Account2 = new EntityReference("account", Guid.NewGuid());
        private static readonly EntityReference Contact = new EntityReference("contact", Guid.NewGuid());
        private static readonly EntityReference Contact2 = new EntityReference("contact", Guid.NewGuid());

        private static LocalPluginContext Inner(EntityReference? target, string relationship, params EntityReference[] related)
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();
            if (target != null)
            {
                inner.PluginExecutionContext.InputParameters["Target"] = target;
            }

            inner.PluginExecutionContext.InputParameters["Relationship"] = new Relationship(relationship);
            inner.PluginExecutionContext.InputParameters["RelatedEntities"] = new EntityReferenceCollection(related);
            return inner;
        }

        [Fact]
        public void Target_of_the_plugin_table_is_this_side()
        {
            RelationshipSides sides = new AssociatePostContext<TestAccount>(Inner(Account, "account_contacts", Contact, Contact2)).Sides();

            Assert.Equal("account_contacts", sides.Relationship!.SchemaName);
            Assert.Same(Account, Assert.Single(sides.ThisSide));
            Assert.Equal(new[] { Contact, Contact2 }, sides.OtherSide);
        }

        [Fact]
        public void Target_of_the_other_table_puts_the_related_records_on_this_side()
        {
            RelationshipSides sides = new AssociatePreContext<TestAccount>(Inner(Contact, "account_contacts", Account, Account2)).Sides();

            Assert.Equal(new[] { Account, Account2 }, sides.ThisSide);
            Assert.Same(Contact, Assert.Single(sides.OtherSide));
        }

        [Fact]
        public void Disassociate_contexts_split_the_same_way_from_either_end()
        {
            RelationshipSides fromAccount = new DisassociatePostContext<TestAccount>(Inner(Account, "account_contacts", Contact)).Sides();
            RelationshipSides fromContact = new DisassociatePreContext<TestAccount>(Inner(Contact, "account_contacts", Account)).Sides();

            Assert.Equal(fromAccount.ThisSide.Select(r => r.Id), fromContact.ThisSide.Select(r => r.Id));
            Assert.Equal(fromAccount.OtherSide.Select(r => r.Id), fromContact.OtherSide.Select(r => r.Id));
            Assert.Equal(Account.Id, Assert.Single(fromAccount.ThisSide).Id);
        }

        [Fact]
        public void Self_referential_relationship_keeps_the_target_on_this_side()
        {
            RelationshipSides sides = new AssociatePostContext<TestAccount>(Inner(Account, "account_parent", Account2)).Sides();

            Assert.Same(Account, Assert.Single(sides.ThisSide));
            Assert.Same(Account2, Assert.Single(sides.OtherSide));
        }

        [Fact]
        public void Relationship_without_the_plugin_table_leaves_this_side_empty()
        {
            EntityReference lead = new EntityReference("lead", Guid.NewGuid());
            RelationshipSides sides = new AssociatePostContext<TestAccount>(Inner(Contact, "contact_leads", lead)).Sides();

            Assert.Empty(sides.ThisSide);
            Assert.Equal(new[] { Contact, lead }, sides.OtherSide);
        }

        [Fact]
        public void Table_match_ignores_case_and_null_entries_are_dropped()
        {
            EntityReference upper = new EntityReference("ACCOUNT", Guid.NewGuid());
            LocalPluginContext inner = Inner(Contact, "account_contacts");
            inner.PluginExecutionContext.InputParameters["RelatedEntities"] = new EntityReferenceCollection { upper, null! };

            RelationshipSides sides = new AssociatePostContext<TestAccount>(inner).Sides();

            Assert.Same(upper, Assert.Single(sides.ThisSide));
            Assert.Same(Contact, Assert.Single(sides.OtherSide));
        }

        [Fact]
        public void Missing_inputs_give_empty_sides()
        {
            LocalPluginContext inner = new PluginContextBuilder().Build();

            RelationshipSides sides = new AssociatePostContext<TestAccount>(inner).Sides();

            Assert.Null(sides.Relationship);
            Assert.Empty(sides.ThisSide);
            Assert.Empty(sides.OtherSide);
        }

        [Fact]
        public void Null_context_throws()
        {
            Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((IAssociatePreContext<TestAccount>)null!).Sides()).ParamName);
            Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((IAssociatePostContext<TestAccount>)null!).Sides()).ParamName);
            Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((IDisassociatePreContext<TestAccount>)null!).Sides()).ParamName);
            Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => ((IDisassociatePostContext<TestAccount>)null!).Sides()).ParamName);
        }
    }
}
