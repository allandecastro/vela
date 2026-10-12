using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// One way per message: a message with a typed On{Message}() trigger is registered through it, and On<TRequest>() /
// OnAsyncOnly<TRequest>() reject it. The list of typed triggers is generated from the message model.
public sealed class OneWayStepTriggerTests
{
    // A request whose RequestName is chosen per test, so every typed-trigger message can be tried through On<T>().
    public sealed class NamedRequest : OrganizationRequest
    {
        [ThreadStatic] internal static string? Next;

        public NamedRequest()
        {
            RequestName = Next ?? "vela_custom";
        }
    }

    public static IEnumerable<object[]> TypedTriggerMessages()
        => TypedTriggers.Messages.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(TypedTriggerMessages))]
    public void Every_message_with_a_typed_trigger_is_rejected_through_On_TRequest(string message)
    {
        // The generated list names real triggers: each one exists on IStepTrigger<TEntity>.
        Assert.NotNull(typeof(IStepTrigger<>).GetMethod("On" + message, Type.EmptyTypes));

        NamedRequest.Next = message;
        try
        {
            IStepTrigger<TestAccount> trigger = new StepBuilder<TestAccount>();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => trigger.On<NamedRequest>());
            Assert.Contains($"On{message}()", ex.Message);

            InvalidOperationException asyncOnly = Assert.Throws<InvalidOperationException>(() => ((IStepTrigger<TestAccount>)new StepBuilder<TestAccount>()).OnAsyncOnly<NamedRequest>());
            Assert.Contains("OnAsyncOnly<NamedRequest>()", asyncOnly.Message);
        }
        finally
        {
            NamedRequest.Next = null;
        }
    }

    [Fact]
    public void Every_typed_trigger_on_the_api_is_in_the_generated_list()
    {
        string[] triggers = typeof(IStepTrigger<>).GetMethods()
            .Where(m => m.Name.StartsWith("On", StringComparison.Ordinal) && m.Name != "OnMessage" && !m.IsGenericMethod && m.GetParameters().Length == 0)
            .Select(m => m.Name.Substring(2))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(triggers, TypedTriggers.Messages.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_message_without_a_typed_trigger_registers_through_On_TRequest()
    {
        NamedRequest.Next = "vela_Recalculate";
        try
        {
            StepBuilder<TestAccount> builder = new();
            ((IStepTrigger<TestAccount>)builder).On<NamedRequest>().PostOperation().Sync().Handle(_ => { });
            Assert.Equal("vela_Recalculate", builder.Build().Message);
        }
        finally
        {
            NamedRequest.Next = null;
        }
    }

    [Fact]
    public void A_plugin_registering_On_UpdateRequest_fails_with_the_trigger_to_use()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new UpdateThroughTypedRequest().GetStepDefinitions());
        Assert.Contains("register the step with OnUpdate() instead of On<UpdateRequest>()", ex.Message);
    }

    [Fact]
    public void The_rejection_names_a_generic_request_as_CSharp_spells_it()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new CreateThroughGenericRequest().GetStepDefinitions());
        Assert.Contains("register the step with OnCreate() instead of On<CreateRequest<TestAccount>>()", ex.Message);
        Assert.DoesNotContain("`", ex.Message);
    }

    private sealed class CreateThroughGenericRequest : EntityPluginBase<TestAccount>
    {
        public CreateThroughGenericRequest() : base(string.Empty, string.Empty) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().On<Vela.Messages.CreateRequest<TestAccount>>().PostOperation().Sync().Handle(_ => { });
    }

    private sealed class UpdateThroughTypedRequest : EntityPluginBase<TestAccount>
    {
        public UpdateThroughTypedRequest() : base(string.Empty, string.Empty) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().On<Microsoft.Xrm.Sdk.Messages.UpdateRequest>().PostOperation().Sync().Handle(_ => { });
    }
}
