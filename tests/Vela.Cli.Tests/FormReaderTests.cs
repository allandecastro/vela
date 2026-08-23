using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

public class FormReaderTests
{
    private static Entity Form(string entity, string name, int type, string xml, int state = 1)
    {
        Entity form = new Entity("systemform", Guid.NewGuid());
        form["name"] = name;
        form["objecttypecode"] = entity;
        form["type"] = new OptionSetValue(type);
        form["formxml"] = xml;
        form["formactivationstate"] = new OptionSetValue(state);
        return form;
    }

    [Fact]
    public void ReadForms_Queries_SystemForm_With_Scoped_Columns_And_Filters()
    {
        FakeOrgService svc = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(Form("account", "Account", 2, "<form><tabs /></form>")),
        };

        FormReader.ReadForms(svc, new[] { "account", "contact" });

        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Equal("systemform", q.EntityName);
        foreach (string column in new[] { "formid", "name", "objecttypecode", "type", "formxml" })
        {
            Assert.Contains(column, q.ColumnSet.Columns);
        }

        Assert.Contains(q.Criteria.Conditions, c => c.AttributeName == "objecttypecode" && c.Operator == ConditionOperator.In);
        Assert.Contains(q.Criteria.Conditions, c => c.AttributeName == "type" && c.Operator == ConditionOperator.In);
        Assert.Contains(q.Criteria.Conditions, c => c.AttributeName == "formactivationstate" && c.Operator == ConditionOperator.Equal);
    }

    [Fact]
    public void ReadForms_Maps_And_Orders_By_Table_Type_Name()
    {
        FakeOrgService svc = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(
                Form("contact", "Contact Main", 2, "<form><tabs /></form>"),
                Form("account", "Account Quick", 6, "<form><tabs /></form>"),
                Form("account", "Account Main", 2, "<form><tabs /></form>")),
        };

        var forms = FormReader.ReadForms(svc, new[] { "account", "contact" });

        Assert.Equal(new[] { "Account Main", "Account Quick", "Contact Main" }, forms.Select(f => f.Name));
        Assert.Equal(new[] { "Main", "QuickCreate", "Main" }, forms.Select(f => f.FormType));
    }

    [Fact]
    public void ReadForms_Skips_Unknown_Types_And_Empty_FormXml()
    {
        FakeOrgService svc = new FakeOrgService
        {
            OnRetrieveMultiple = _ => FakeOrgService.Collection(
                Form("account", "Card", 11, "<form><tabs /></form>"), // unknown type -> skipped
                Form("account", "Blank", 2, "   "),                    // empty formxml -> skipped
                Form("account", "Main", 2, "<form><tabs /></form>")),
        };

        SystemForm only = Assert.Single(FormReader.ReadForms(svc, new[] { "account" }));
        Assert.Equal("Main", only.Name);
    }

    [Fact]
    public void ReadForms_Skips_Rows_Without_ObjectTypeCode()
    {
        Entity form = new Entity("systemform", Guid.NewGuid());
        form["name"] = "Orphan";
        form["type"] = new OptionSetValue(2);
        form["formxml"] = "<form><tabs /></form>";
        // no objecttypecode
        FakeOrgService svc = new FakeOrgService { OnRetrieveMultiple = _ => FakeOrgService.Collection(form) };

        Assert.Empty(FormReader.ReadForms(svc, new[] { "account" }));
    }

    [Fact]
    public void ReadForms_With_No_Entities_Returns_Empty_Without_Querying()
    {
        FakeOrgService svc = new FakeOrgService();

        Assert.Empty(FormReader.ReadForms(svc, Array.Empty<string>()));
        Assert.Empty(svc.Queries);
    }

    [Theory]
    [InlineData(2, "Main")]
    [InlineData(6, "QuickCreate")]
    [InlineData(7, null)]
    public void MapFormType_Maps_Known_Types(int type, string? expected)
    {
        Assert.Equal(expected, FormReader.MapFormType(type));
    }
}
