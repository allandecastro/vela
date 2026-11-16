using System;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The permissive Handle(Delegate) overload compiles any one-parameter method, so the context a handler takes is
// checked against the one the dispatch builds for the step when the steps are registered: a mismatch fails there,
// naming the expected context, instead of failing every execution with a raw cast error.
public sealed class HandlerContextRegistrationTests
{
    private sealed class DelegatePlugin(Action<IStepTrigger<TestAccount>> register) : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps() => register(RegisterStep());
    }

    private const string DoThing = "acme_DoThing";

    private sealed class DoThingRequest : OrganizationRequest
    {
        public DoThingRequest() => RequestName = DoThing;
    }

    private sealed class OtherRequest : OrganizationRequest
    {
        public OtherRequest() => RequestName = "acme_Other";
    }

    // A request class that names no message: only a typed view over the parameters.
    private sealed class UnnamedRequest : OrganizationRequest
    {
    }

    // What the source generator emits for a handler of an OnAssign() post-operation step.
    private sealed class OnAssignedContext(ILocalPluginContext inner) : AssignPostContext<TestAccount>(inner);

    // A generated context for a request-typed step.
    private sealed class OnDoThingContext(ILocalPluginContext inner) : MessagePostContext<DoThingRequest, TestAccount>(inner);

    private static Delegate Handler<TContext>() => new Action<TContext>(_ => { });

    private static string Error(Action<IStepTrigger<TestAccount>> register) =>
        Assert.Throws<InvalidPluginExecutionException>(() => new DelegatePlugin(register).GetStepDefinitions()).Message;

    private static void Accepts(Action<IStepTrigger<TestAccount>> register) =>
        Assert.Single(new DelegatePlugin(register).GetStepDefinitions());

    [Fact]
    public void An_update_context_on_an_assign_step_fails_at_registration_naming_the_assign_context()
    {
        string message = Error(s => s.OnAssign().PostOperation().Sync().Handle(Handler<IUpdatePostContext<TestAccount>>()));

        Assert.Contains("is bound to the Assign PostOperation step but takes 'IUpdatePostContext<TestAccount>'", message);
        Assert.Contains("This step passes IAssignPostContext<TestAccount>", message);
    }

    [Fact]
    public void A_post_context_on_a_pre_stage_of_the_same_message_fails()
    {
        string message = Error(s => s.OnAssign().PreOperation().Sync().Handle(Handler<IAssignPostContext<TestAccount>>()));

        Assert.Contains("This step passes IAssignPreContext<TestAccount>", message);
    }

    [Fact]
    public void A_context_for_another_entity_fails()
    {
        string message = Error(s => s.OnAssign().PostOperation().Sync().Handle(Handler<IAssignPostContext<Entity>>()));

        Assert.Contains("takes 'IAssignPostContext<Entity>'", message);
    }

    [Fact]
    public void The_message_context_the_plain_context_and_a_generated_context_are_accepted()
    {
        Accepts(s => s.OnAssign().PostOperation().Sync().Handle(Handler<IAssignPostContext<TestAccount>>()));
        Accepts(s => s.OnAssign().PostOperation().Sync().Handle(Handler<ILocalPluginContext>()));
        Accepts(s => s.OnAssign().PostOperation().Sync().Handle(Handler<OnAssignedContext>()));
        Accepts(s => s.OnRetrieve().PreOperation().Sync().Handle(Handler<IRetrievePreContext<TestAccount>>()));
    }

    [Fact]
    public void A_generated_context_of_another_message_fails()
    {
        string message = Error(s => s.OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(Handler<OnAssignedContext>()));

        Assert.Contains("This step passes IUpdatePostContext<TestAccount>", message);
    }

    [Fact]
    public void A_generic_message_context_on_a_modelled_message_fails()
    {
        string message = Error(s => s.OnAssign().PostOperation().Sync().Handle(Handler<IMessagePostContext<Microsoft.Crm.Sdk.Messages.AssignRequest>>()));

        Assert.Contains("a generic message context is for a message without a typed trigger", message);
    }

    [Fact]
    public void A_modelled_context_on_an_unmodelled_message_fails()
    {
        string message = Error(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle(Handler<IUpdatePostContext<TestAccount>>()));

        Assert.Contains("This step passes ILocalPluginContext, or IMessagePostContext<TRequest> for the acme_DoThing request", message);
    }

    [Fact]
    public void A_generated_modelled_context_on_an_unmodelled_message_fails()
    {
        string message = Error(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle(Handler<OnAssignedContext>()));

        Assert.Contains("it is a context for a modelled message, not this one", message);
    }

    [Fact]
    public void Handle_TRequest_with_the_request_of_another_message_fails()
    {
        string message = Error(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle<OtherRequest>(_ => { }));

        Assert.Contains("its request OtherRequest is the acme_Other message's, not this step's", message);
    }

    [Fact]
    public void Handle_TRequest_with_the_request_of_the_message_or_an_unnamed_request_is_accepted()
    {
        Accepts(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle<DoThingRequest>(_ => { }));
        Accepts(s => s.OnMessage<AcmeDoThing>().PreOperation().Sync().Handle<DoThingRequest>(_ => { }));
        Accepts(s => s.OnMessage<DoThingRequest>().PostOperation().Sync().Handle<DoThingRequest>(_ => { }));
        Accepts(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle<UnnamedRequest>(_ => { }));
        Accepts(s => s.OnMessage<AcmeDoThing>().PostOperation().Sync().Handle(Handler<OnDoThingContext>()));
        Accepts(s => s.On<DoThingRequest>().PostOperation().Sync().Handle(_ => { }));
    }

    [Fact]
    public void A_generated_context_of_another_request_fails()
    {
        string message = Error(s => s.OnMessage<OtherRequest>().PostOperation().Sync().Handle(Handler<OnDoThingContext>()));

        Assert.Contains("its request DoThingRequest is the acme_DoThing message's", message);
    }

    [Fact]
    public void A_post_message_context_at_a_pre_stage_fails()
    {
        string message = Error(s => s.OnMessage<AcmeDoThing>().PreOperation().Sync().Handle(Handler<IMessagePostContext<DoThingRequest>>()));

        Assert.Contains("a post-operation context has no response before the main operation", message);
        Assert.Contains("IMessagePreContext<TRequest>", message);
    }
}
