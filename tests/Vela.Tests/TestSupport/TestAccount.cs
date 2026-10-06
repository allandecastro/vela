using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Tests.TestSupport;

[EntityLogicalName(EntityLogicalNameValue)]
public sealed class TestAccount : Entity
{
    public const string EntityLogicalNameValue = "account";

    public TestAccount()
        : base(EntityLogicalNameValue)
    {
    }

    [AttributeLogicalName("accountid")]
    public Guid? AccountId
    {
        get => GetAttributeValue<Guid?>("accountid");
        set => SetAttributeValue("accountid", value);
    }

    [AttributeLogicalName("name")]
    public string Name
    {
        get => GetAttributeValue<string>("name");
        set => SetAttributeValue("name", value);
    }

    // Partylist-style column typed as EntityCollection (mirrors how ActivityParty columns are generated).
    [AttributeLogicalName("vela_tags")]
    public OptionSetValueCollection? Tags
    {
        get => GetAttributeValue<OptionSetValueCollection>("vela_tags");
        set => SetAttributeValue("vela_tags", value);
    }

    [AttributeLogicalName("customers")]
    public EntityCollection Customers
    {
        get => GetAttributeValue<EntityCollection>("customers");
        set => SetAttributeValue("customers", value);
    }
}
