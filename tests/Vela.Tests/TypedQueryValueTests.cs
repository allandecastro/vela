using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Typed query values reach Dataverse in the shape it compares: enums as integers, date-times with their zone and
// milliseconds, and several related-record filters combined instead of the last one winning.
public sealed class TypedQueryValueTests
{
    private enum Tier
    {
        Standard = 0,
        Gold = 3,
    }

    [EntityLogicalName("contact")]
    private sealed class Contact : Entity
    {
        public Contact()
            : base("contact")
        {
        }

        [AttributeLogicalName("fullname")]
        public string? FullName
        {
            get => GetAttributeValue<string>("fullname");
            set => SetAttributeValue("fullname", value);
        }

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference? ParentCustomerId
        {
            get => GetAttributeValue<EntityReference>("parentcustomerid");
            set => SetAttributeValue("parentcustomerid", value);
        }
    }

    [Fact]
    public void An_enum_condition_value_becomes_its_integer_in_the_query_and_in_fetchxml()
    {
        TypedQuery<TestAccount> query = Query.For<TestAccount>()
            .Select(a => a.Name)
            .Where(a => a.Name, ConditionOperator.Equal, Tier.Gold);

        Assert.Equal(3, query.Expression.Criteria.Conditions[0].Values[0]);
        Assert.Contains("value=\"3\"", query.ToFetchXml());
        Assert.DoesNotContain("Gold", query.ToFetchXml());
    }

    [Fact]
    public void Enum_values_are_normalized_in_every_typed_condition_helper()
    {
        TypedQuery<TestAccount> query = Query.For<TestAccount>()
            .WhereIn(a => a.Name, new[] { Tier.Standard, Tier.Gold })
            .WhereAll(f => f.Condition(a => a.Name, ConditionOperator.Equal, Tier.Gold).ConditionIn(a => a.Name, new[] { Tier.Gold }))
            .Link<Contact>(a => a.AccountId, c => c.ParentCustomerId, configure: l => l.Where(c => c.FullName, ConditionOperator.Equal, Tier.Gold));

        Assert.Equal(new object[] { 0, 3 }, query.Expression.Criteria.Conditions[0].Values.ToArray());
        Assert.Equal(3, query.Expression.Criteria.Filters[0].Conditions[0].Values[0]);
        Assert.Equal(3, query.Expression.Criteria.Filters[0].Conditions[1].Values[0]);
        Assert.Equal(3, query.Expression.LinkEntities[0].LinkCriteria.Conditions[0].Values[0]);

        QueryExpression raw = new QueryExpression("account").Where<TestAccount>(a => a.Name, ConditionOperator.Equal, Tier.Gold);
        Assert.Equal(3, raw.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void A_utc_DateTime_is_written_with_milliseconds_and_Z()
    {
        DateTime utc = new DateTime(2026, 10, 7, 13, 45, 30, 123, DateTimeKind.Utc);
        QueryExpression query = new QueryExpression("account");
        query.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, utc);

        Assert.Contains("value=\"2026-10-07T13:45:30.123Z\"", query.ToFetchXml());
    }

    [Fact]
    public void A_local_DateTime_is_converted_to_utc()
    {
        DateTime local = new DateTime(2026, 10, 7, 13, 45, 30, 123, DateTimeKind.Local);
        string expected = local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        QueryExpression query = new QueryExpression("account");
        query.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, local);

        Assert.Contains($"value=\"{expected}\"", query.ToFetchXml());
    }

    [Fact]
    public void An_unspecified_DateTime_keeps_its_milliseconds_and_states_no_zone()
    {
        DateTime unspecified = new DateTime(2026, 10, 7, 13, 45, 30, 120, DateTimeKind.Unspecified);
        QueryExpression query = new QueryExpression("account");
        query.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, unspecified);

        Assert.Contains("value=\"2026-10-07T13:45:30.120\"", query.ToFetchXml());
    }

    [Fact]
    public void A_DateTimeOffset_keeps_its_offset()
    {
        DateTimeOffset value = new DateTimeOffset(2026, 10, 7, 13, 45, 30, 5, TimeSpan.FromHours(2));
        QueryExpression query = new QueryExpression("account");
        query.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, value);

        Assert.Contains("value=\"2026-10-07T13:45:30.005+02:00\"", query.ToFetchXml());
    }

    [Fact]
    public void Two_WhereRelated_filters_combine_instead_of_the_second_replacing_the_first()
    {
        TypedQuery<TestAccount> query = Query.For<TestAccount>()
            .Select(a => a.Name)
            .WhereRelated<Contact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.Any, l => l.Where(c => c.FullName, ConditionOperator.Equal, "Ann"))
            .WhereRelated<Contact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.NotAny, l => l.Where(c => c.FullName, ConditionOperator.Equal, "Bob"));

        Assert.Null(query.Expression.Criteria.AnyAllFilterLinkEntity);
        Assert.Equal(2, query.Expression.Criteria.Filters.Count);
        Assert.Equal(JoinOperator.Any, query.Expression.Criteria.Filters[0].AnyAllFilterLinkEntity.JoinOperator);
        Assert.Equal(JoinOperator.NotAny, query.Expression.Criteria.Filters[1].AnyAllFilterLinkEntity.JoinOperator);

        string xml = query.ToFetchXml();
        Assert.Contains("link-type=\"any\"", xml);
        Assert.Contains("link-type=\"not any\"", xml);

        // Executed: only the account with an Ann and no Bob matches both filters.
        Guid annOnly = Guid.NewGuid();
        Guid annAndBob = Guid.NewGuid();
        Guid nobody = Guid.NewGuid();
        InMemoryOrganizationService org = new InMemoryOrganizationService().Seed(
            new Entity("account", annOnly) { ["name"] = "AnnOnly" },
            new Entity("account", annAndBob) { ["name"] = "AnnAndBob" },
            new Entity("account", nobody) { ["name"] = "Nobody" },
            new Entity("contact", Guid.NewGuid()) { ["fullname"] = "Ann", ["parentcustomerid"] = new EntityReference("account", annOnly) },
            new Entity("contact", Guid.NewGuid()) { ["fullname"] = "Ann", ["parentcustomerid"] = new EntityReference("account", annAndBob) },
            new Entity("contact", Guid.NewGuid()) { ["fullname"] = "Bob", ["parentcustomerid"] = new EntityReference("account", annAndBob) });

        string[] names = org.RetrieveMultiple(query).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray();
        Assert.Equal(new[] { "AnnOnly" }, names);
    }
}
