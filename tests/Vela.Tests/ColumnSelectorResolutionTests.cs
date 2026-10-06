using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// One resolver backs FilterOn, the image selectors and the typed query helpers: it must name the selected column or
// fail, never register a lookalike ("id", "name") for a selector that is not a column of the entity.
public sealed class ColumnSelectorResolutionTests
{
    private sealed class Contact : Entity
    {
        public Contact() : base("contact") { }

        [AttributeLogicalName("contactid")]
        public override Guid Id { get => base.Id; set => base.Id = value; }

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference? ParentCustomerId { get => GetAttributeValue<EntityReference>("parentcustomerid"); set => SetAttributeValue("parentcustomerid", value); }

        [AttributeLogicalName("parentaccount")]
        public TestAccount? Parent => null;
    }

    [Fact]
    public void A_column_resolves_to_its_logical_name()
    {
        Assert.Equal("parentcustomerid", ExpressionHelper.GetAttributeName<Contact>(c => c.ParentCustomerId));
        Assert.Equal("contactid", ExpressionHelper.GetAttributeName<Contact>(c => c.Id)); // the override names the primary key
    }

    [Fact]
    public void A_nested_path_is_rejected()
    {
        ArgumentException lookupId = Assert.Throws<ArgumentException>(() => ExpressionHelper.GetAttributeName<Contact>(c => c.ParentCustomerId!.Id));
        Assert.Contains("nested path", lookupId.Message);
        Assert.Throws<ArgumentException>(() => ExpressionHelper.GetAttributeLogicalName<Contact>(c => c.Parent!.Name));
    }

    [Fact]
    public void A_base_Entity_member_without_a_column_name_is_rejected()
    {
        // TestAccount does not override Id: `a => a.Id` used to register a nonexistent "id" column.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ExpressionHelper.GetAttributeName<TestAccount>(a => a.Id));
        Assert.Contains("member of Entity", ex.Message);
        Assert.Throws<ArgumentException>(() => ExpressionHelper.GetAttributeName<TestAccount>(a => a.LogicalName));
    }
}
