using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Messages;

namespace ContractPluginFixture
{
    [EntityLogicalName("account")]
    public sealed class ContractAccount : Entity
    {
        public ContractAccount() : base("account") { }

        [AttributeLogicalName("accountid")]
        public Guid? AccountId { get => GetAttributeValue<Guid?>("accountid"); set => SetAttributeValue("accountid", value); }

        [AttributeLogicalName("name")]
        public string? Name { get => GetAttributeValue<string>("name"); set => SetAttributeValue("name", value); }

        [AttributeLogicalName("telephone1")]
        public string? Telephone1 { get => GetAttributeValue<string>("telephone1"); set => SetAttributeValue("telephone1", value); }

        [AttributeLogicalName("revenue")]
        public Money? Revenue { get => GetAttributeValue<Money>("revenue"); set => SetAttributeValue("revenue", value); }

        [AttributeLogicalName("creditlimit")]
        public Money? CreditLimit { get => GetAttributeValue<Money>("creditlimit"); set => SetAttributeValue("creditlimit", value); }
    }

    [EntityLogicalName("contact")]
    public sealed class ContractContact : Entity
    {
        public ContractContact() : base("contact") { }

        [AttributeLogicalName("contactid")]
        public Guid? ContactId { get => GetAttributeValue<Guid?>("contactid"); set => SetAttributeValue("contactid", value); }

        [AttributeLogicalName("lastname")]
        public string? LastName { get => GetAttributeValue<string>("lastname"); set => SetAttributeValue("lastname", value); }
    }

    // A global Custom API's request, marked as `vela models --custom-apis` marks it: a step declared from it
    // registers with no table, whatever the plugin's table.
    [GlobalCustomApi]
    public sealed class PingRequest : CustomApiRequest
    {
        public PingRequest() : base("contract_Ping") { }
    }

    public sealed class ScoreAccountRequest : CustomApiRequest
    {
        public ScoreAccountRequest() : base("contract_ScoreAccount") { }

        public string? Reason { get => Get<string>(); set => Set(value); }

        public int Weight { get => Get<int>(); set => Set(value); }

        public bool Strict { get => Get<bool>(); set => Set(value); }

        public Guid CorrelationKey { get => Get<Guid>(); set => Set(value); }

        public DateTime AsOf { get => Get<DateTime>(); set => Set(value); }

        public Money? Budget { get => Get<Money>(); set => Set(value); }

        public decimal Factor { get => Get<decimal>(); set => Set(value); }

        public OptionSetValue? Tier { get => Get<OptionSetValue>(); set => Set(value); }
    }

    public sealed class ScoreAccountResponse : CustomApiResponse
    {
        public ScoreAccountResponse() : base("contract_ScoreAccount") { }

        public int Score { get => Get<int>(); set => Set(value); }

        public string? Explanation { get => Get<string>(); set => Set(value); }
    }
}
