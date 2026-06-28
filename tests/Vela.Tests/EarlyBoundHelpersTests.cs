using System;
using Vela.Context;
using Vela.Querying;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class EarlyBoundHelpersTests
    {
        [Fact]
        public void GetTarget_ReturnsTypedEntity()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();
            Entity target = new Entity(TestAccount.EntityLogicalNameValue) { Id = Guid.NewGuid() };
            target["name"] = "Account Name";
            context.PluginExecutionContext.InputParameters["Target"] = target;

            TestAccount typed = context.GetTarget<TestAccount>();
            Assert.Equal("Account Name", typed.Name);
        }

        [Fact]
        public void Contains_WithTypedExpression_ChecksAttributePresence()
        {
            TestAccount account = new TestAccount();
            account["name"] = "Account Name";

            Assert.True(account.Contains(x => x.Name));
            Assert.False(account.Contains(x => x.AccountId));
        }

        [Fact]
        public void Contains_WithMultipleTypedExpressions_RequiresAllAttributes()
        {
            TestAccount account = new TestAccount();
            account["name"] = "Account Name";

            Assert.False(account.Contains(x => x.Name, x => x.AccountId));

            account["accountid"] = Guid.NewGuid();
            Assert.True(account.Contains(x => x.Name, x => x.AccountId));
        }

        [Fact]
        public void ContainsAny_RequiresAtLeastOneAttribute()
        {
            TestAccount account = new TestAccount();
            account["name"] = "Account Name";

            // 'name' is present, 'accountid' is not — any-of is satisfied.
            Assert.True(account.ContainsAny(x => x.AccountId, x => x.Name));

            TestAccount empty = new TestAccount();
            Assert.False(empty.ContainsAny(x => x.AccountId, x => x.Name));
        }
    }
}
