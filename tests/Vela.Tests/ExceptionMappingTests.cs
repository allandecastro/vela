using System;
using System.ServiceModel;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.Testing;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class ExceptionMappingTests
    {
        [Fact]
        public void PluginBase_MapsBusinessExceptionToCleanInvalidPluginExecutionException()
        {
            ThrowingPlugin plugin = new ThrowingPlugin(new PluginBusinessException("Clean business message."));
            IServiceProvider provider = new PluginContextBuilder().BuildServiceProvider();

            Action act = () => plugin.Execute(provider);
            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Equal("Clean business message.", exception.Message);
        }

        [Fact]
        public void PluginBase_TracesTheBusinessExceptionWithItsStackAndInnerException()
        {
            RecordingTracingService tracing = new RecordingTracingService();
            ThrowingPlugin plugin = new ThrowingPlugin(
                new PluginBusinessException("Clean business message.", new InvalidOperationException("root-cause")));
            IServiceProvider provider = new PluginContextBuilder().WithTracingService(tracing).BuildServiceProvider();

            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

            Assert.Equal("Clean business message.", exception.Message);
            Assert.Contains("Business exception: Vela.Plugin.PluginBusinessException: Clean business message.", tracing.Output);
            Assert.Contains("root-cause", tracing.Output);
            Assert.Contains(nameof(ThrowingPlugin), tracing.Output); // a stack frame of the throwing handler
        }

        [Fact]
        public void PluginBase_PassesTheBusinessErrorCodeToTheInvalidPluginExecutionException()
        {
            ThrowingPlugin plugin = new ThrowingPlugin(new PluginBusinessException("Coded business message.", 12345));
            IServiceProvider provider = new PluginContextBuilder().BuildServiceProvider();

            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

            Assert.Equal("Coded business message.", exception.Message);
            Assert.Equal(12345, exception.ErrorCode);
            Assert.Equal(OperationStatus.Failed, exception.Status);
        }

        [Fact]
        public void PluginBase_RethrowsAnOrganizationServiceFaultUnchanged()
        {
            // Dataverse returns a plug-in's FaultException<OrganizationServiceFault> to the caller with its own
            // ErrorCode, so the base must not wrap it (a wrapper would surface as the generic 0x80040265).
            RecordingTracingService tracing = new RecordingTracingService();
            FaultException<OrganizationServiceFault> fault = new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = -2147220937, Message = "Cannot insert duplicate key." },
                "Cannot insert duplicate key.");
            ThrowingPlugin plugin = new ThrowingPlugin(fault);
            IServiceProvider provider = new PluginContextBuilder().WithTracingService(tracing).BuildServiceProvider();

            FaultException<OrganizationServiceFault> thrown =
                Assert.Throws<FaultException<OrganizationServiceFault>>(() => plugin.Execute(provider));

            Assert.Same(fault, thrown);
            Assert.Equal(-2147220937, thrown.Detail.ErrorCode);
            Assert.Contains("Organization service fault", tracing.Output);
        }

        [Fact]
        public void PluginBase_HidesTheUnhandledExceptionTypeAndMessageFromTheCaller()
        {
            // The caller sees a generic message with the correlation id; the type and raw message (which can carry
            // record values or endpoints) go to the trace log only, and the original stays the inner exception.
            RecordingTracingService tracing = new RecordingTracingService();
            Guid correlationId = Guid.NewGuid();
            ThrowingPlugin plugin = new ThrowingPlugin(new InvalidOperationException("boom-detail"));
            IServiceProvider provider = new PluginContextBuilder()
                .WithCorrelationId(correlationId)
                .WithTracingService(tracing)
                .BuildServiceProvider();

            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

            Assert.Contains(correlationId.ToString(), exception.Message);
            Assert.DoesNotContain("boom-detail", exception.Message);
            Assert.DoesNotContain(nameof(InvalidOperationException), exception.Message);
            Assert.DoesNotContain(typeof(ThrowingPlugin).FullName!, exception.Message);
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains("InvalidOperationException: boom-detail", tracing.Output);
        }

        [Fact]
        public void PluginBase_UsesTheOverriddenUnexpectedErrorMessage()
        {
            DetailedMessagePlugin plugin = new DetailedMessagePlugin(_ => "detailed: " + _.Message);
            IServiceProvider provider = new PluginContextBuilder().BuildServiceProvider();

            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

            Assert.Equal("detailed: boom-detail", exception.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PluginBase_FallsBackToTheDefaultMessageWhenTheOverrideFails(bool overrideThrows)
        {
            Guid correlationId = Guid.NewGuid();
            DetailedMessagePlugin plugin = new DetailedMessagePlugin(
                _ => overrideThrows ? throw new FormatException("formatter broke") : string.Empty);
            IServiceProvider provider = new PluginContextBuilder().WithCorrelationId(correlationId).BuildServiceProvider();

            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));

            Assert.Contains(correlationId.ToString(), exception.Message);
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        [Fact]
        public void CustomApi_HandlerBusinessException_PropagatesUnwrapped()
        {
            // The lightweight Custom API handler is invoked via reflection. Its business exception must surface
            // unwrapped (not enclosed in a TargetInvocationException), otherwise the Dataverse error mapping breaks.
            ThrowingCustomApi api = new ThrowingCustomApi();
            LocalPluginContext context = PluginContext.ForMessage("zava_Throw").Build();

            Action act = () => api.Execute(context.ServiceProvider);
            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Equal("Clean business message.", exception.Message);
        }

        private sealed class ThrowingPlugin(Exception toThrow) : PluginBase(string.Empty, string.Empty)
        {
            protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) => throw toThrow;
        }

        private sealed class DetailedMessagePlugin(Func<Exception, string> format) : PluginBase(string.Empty, string.Empty)
        {
            protected override void ExecuteDataversePlugin(ILocalPluginContext ctx) =>
                throw new InvalidOperationException("boom-detail");

            protected override string FormatUnexpectedErrorMessage(ILocalPluginContext context, Exception exception) =>
                format(exception);
        }

        private sealed class ThrowingCustomApi : CustomApiBase
        {
            public ThrowingCustomApi()
                : base(string.Empty, string.Empty)
            {
            }

            protected override void RegisterCustomApi()
            {
                RegisterCustomApi("zava_Throw", nameof(Handle));
            }

            private void Handle(ILocalPluginContext ctx)
            {
                throw new PluginBusinessException("Clean business message.");
            }
        }
    }
}
